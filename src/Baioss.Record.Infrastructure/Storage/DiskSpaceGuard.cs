using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>Severidad del espacio en disco (peor → mayor valor): Ok, Bajo, Crítico, Emergencia. (Emergencia = Fase 3.)</summary>
public enum DiskLevel { Ok, Low, Critical, Emergency }

/// <summary>
/// Vigila el disco de destino mientras se graba: cada <see cref="PollInterval"/> mide el espacio libre
/// de la unidad y estima el tiempo de grabación restante al ritmo de datos actual. Emite
/// <see cref="Updated"/> (para la UI) y, al cruzar umbrales, eleva el nivel a Bajo o Crítico. El crítico
/// está pensado para DETENER la grabación de forma ordenada antes de que el disco se llene y corrompa
/// el archivo (el consumidor decide la acción en <see cref="Updated"/>).
/// </summary>
public sealed class DiskSpaceGuard : IAsyncDisposable
{
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Func<string?> _outputDir = () => null;
    private Func<long> _bytesPerSecond = () => 0;
    private Func<long> _minFreeBytes = () => 0;

    public DiskSpaceGuard(ILogger log) => _log = log;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Aviso (amarillo) cuando el tiempo restante estimado baja de aquí.</summary>
    public TimeSpan WarnRemaining { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Crítico (rojo / auto-stop) cuando el tiempo restante baja de aquí.</summary>
    public TimeSpan CriticalRemaining { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Piso absoluto de espacio libre: por debajo es crítico aunque el ritmo de datos sea bajo.</summary>
    public long MinFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024; // 2 GiB

    // --- Umbrales de alerta por % OCUPADO (Fase 3). INDEPENDIENTES del auto-stop (que sigue atado al piso de
    //     bytes / tiempo restante): estos solo elevan el NIVEL para alertar. 0 = umbral desactivado. Se leen por
    //     DELEGADO para que reflejen EN VIVO los ajustes editables (Fase 4c). ---
    /// <summary>Ocupado ≥ este % → AVISO (Bajo). (Fase 3/4c.)</summary>
    public Func<int> WarnPercent { get; init; } = () => 80;
    /// <summary>Ocupado ≥ este % → CRÍTICO (alerta, NO auto-stop). (Fase 3/4c.)</summary>
    public Func<int> CriticalPercent { get; init; } = () => 90;
    /// <summary>Ocupado ≥ este % → EMERGENCIA. (Fase 3/4c.)</summary>
    public Func<int> EmergencyPercent { get; init; } = () => 95;

    /// <summary>Estado del disco (nivel + libres/restante + si TOCA AUTO-STOP). El auto-stop es la condición dura
    /// "a punto de agotarse" (piso de bytes / tiempo crítico), separada de los niveles por % (alertas). (Fase 3.)</summary>
    public event EventHandler<(DiskLevel Level, StorageInfo Info, bool AutoStop)>? Updated;

    /// <summary>
    /// Arranca la vigilancia. <paramref name="bytesPerSecond"/> debe ser el ritmo AGREGADO del volumen
    /// (todos los canales que escriben en él), y <paramref name="minFreeBytes"/> el piso de espacio
    /// (escalado por nº de canales). Si se omiten, se vigila como un solo canal con el piso fijo
    /// <see cref="MinFreeBytes"/>. (Auditoría 24/7, A7/#10.)
    /// </summary>
    public void Start(Func<string?> outputDir, Func<long> bytesPerSecond, Func<long>? minFreeBytes = null)
    {
        _outputDir = outputDir;
        _bytesPerSecond = bytesPerSecond;
        _minFreeBytes = minFreeBytes ?? (() => MinFreeBytes);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        // Se toman y anulan de golpe (un Detener y el Dispose del canal pueden llegar a la vez).
        var cts = Interlocked.Exchange(ref _cts, null);
        var loop = Interlocked.Exchange(ref _loop, null);
        if (cts is null) return;
        try { await cts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { /* noop */ }
        // Con tope: una medida sobre un NAS colgado puede quedarse bloqueada en el sistema decenas de segundos, y esto va en
        // el camino de Detener (y del apagado). El bucle termina solo al volver la medida (su token ya está cancelado).
        if (loop is not null) { try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* cancelación o tope */ } }
        cts.Dispose();
    }

    /// <summary>Evaluaciones seguidas en condición de auto-stop antes de pedirlo: una medida suelta no para una grabación.</summary>
    public int AutoStopConfirmations { get; init; } = 2;

    private async Task LoopAsync(CancellationToken ct)
    {
        int autoStopStreak = 0;
        DateTimeOffset lastUnmeasurableLog = default;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (free, total) = ReadDrive(_outputDir());
                if (total <= 0)
                {
                    // NO se pudo medir (NAS con un corte momentáneo, disco USB reenumerándose, fallo de la API): antes llegaba
                    // como «0 bytes libres» y DETENÍA la grabación por «disco lleno» con teras libres. Sin medida no se decide
                    // nada: se mantiene el último estado y se avisa (con freno) por si dura.
                    autoStopStreak = 0;
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastUnmeasurableLog > TimeSpan.FromMinutes(5))
                    {
                        lastUnmeasurableLog = now;
                        _log.LogWarning("Guarda de disco: no se puede medir el espacio de «{Dir}»; se reintenta (sin detener la grabación).", _outputDir());
                    }
                }
                else
                {
                    var (level, info, autoStop) = Evaluate(free, total, _bytesPerSecond(), WarnRemaining, CriticalRemaining,
                        _minFreeBytes(), WarnPercent(), CriticalPercent(), EmergencyPercent());
                    autoStopStreak = autoStop ? autoStopStreak + 1 : 0;
                    if (!ct.IsCancellationRequested)
                        Updated?.Invoke(this, (level, info, autoStop && autoStopStreak >= Math.Max(1, AutoStopConfirmations)));
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "Guarda de disco: fallo al medir el espacio."); }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Lee (libres, total) del directorio, con soporte UNC (GetDiskFreeSpaceEx) y respaldo por unidad.
    /// (0,0) si no se puede medir. Lo reutiliza el coordinador de emergencia para vigilar el volumen. (Fase 3b.)</summary>
    internal static (long Free, long Total) ReadDrive(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return (0, 0);
        string full;
        try { full = Path.GetFullPath(dir); } catch { return (0, 0); }

        // GetDiskFreeSpaceEx acepta rutas LOCALES y UNC (\\NAS\share\…): DriveInfo LANZABA con UNC → la guarda de
        // disco quedaba MUDA grabando a un NAS (sin niveles ni auto-stop). Toma un DIRECTORIO (no la raíz de una
        // unidad), así que funciona con la carpeta de grabación tal cual. (Auditoría N15.)
        try
        {
            if (GetDiskFreeSpaceEx(full, out ulong freeForCaller, out ulong total, out _) && total > 0)
                return ((long)freeForCaller, (long)total);
        }
        catch { /* respaldo por unidad abajo */ }

        // Respaldo por unidad (rutas locales con raíz de unidad), por si la API nativa no estuviera disponible.
        try
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
            {
                var d = new DriveInfo(root);
                if (d.IsReady) return (d.AvailableFreeSpace, d.TotalSize);
            }
        }
        catch { /* UNC no soportada por DriveInfo, u otra: sin lectura */ }
        return (0, 0);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
        out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    /// <summary>
    /// Decisión PURA (testeable, sin disco). Devuelve: (1) el NIVEL = el PEOR entre el modelo por tiempo/bytes
    /// (Ok/Bajo/Crítico) y el modelo por % OCUPADO (Ok/Bajo/Crítico/Emergencia, umbrales 0 = off); y (2)
    /// <c>AutoStop</c> = la condición DURA "a punto de agotarse" (piso de bytes libres o tiempo restante crítico),
    /// que es la que DETIENE la grabación para no corromper el archivo — INDEPENDIENTE de los umbrales por %, que
    /// solo alertan. Sin ritmo de datos (sin telemetría) el tiempo restante no se estima. (Fase 3.)
    /// </summary>
    public static (DiskLevel Level, StorageInfo Info, bool AutoStop) Evaluate(
        long freeBytes, long totalBytes, long bytesPerSecond,
        TimeSpan warn, TimeSpan critical, long minFreeBytes,
        int warnPercent = 0, int criticalPercent = 0, int emergencyPercent = 0)
    {
        TimeSpan? remaining = bytesPerSecond > 0
            ? TimeSpan.FromSeconds((double)freeBytes / bytesPerSecond)
            : null;
        var info = new StorageInfo(freeBytes, totalBytes, remaining);

        // AUTO-STOP: piso de bytes o tiempo restante crítico (la grabación va a NO caber → detener). Semántica previa.
        bool autoStop = freeBytes <= minFreeBytes || (remaining is { } rs && rs <= critical);

        // Nivel por TIEMPO/BYTES (modelo previo): Ok / Bajo / Crítico.
        DiskLevel byTime = autoStop ? DiskLevel.Critical
            : (freeBytes <= minFreeBytes * 2 || (remaining is { } rw && rw <= warn)) ? DiskLevel.Low
            : DiskLevel.Ok;

        // Nivel por % OCUPADO (Fase 3): ≥emerg → Emergencia, ≥crit → Crítico, ≥warn → Bajo. 0 desactiva el umbral.
        DiskLevel byPercent = DiskLevel.Ok;
        if (totalBytes > 0)
        {
            double usedPct = (totalBytes - freeBytes) * 100.0 / totalBytes;
            if (emergencyPercent > 0 && usedPct >= emergencyPercent) byPercent = DiskLevel.Emergency;
            else if (criticalPercent > 0 && usedPct >= criticalPercent) byPercent = DiskLevel.Critical;
            else if (warnPercent > 0 && usedPct >= warnPercent) byPercent = DiskLevel.Low;
        }

        var level = (DiskLevel)Math.Max((int)byTime, (int)byPercent);
        return (level, info, autoStop);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
