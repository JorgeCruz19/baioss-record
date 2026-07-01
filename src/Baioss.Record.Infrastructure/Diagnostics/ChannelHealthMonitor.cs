using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.Infrastructure.Diagnostics;

/// <summary>
/// Registra periódicamente la SALUD de los canales que están GRABANDO (fps real vs objetivo, frames perdidos en
/// el intervalo, bitrate y demanda de escritura agregada). Sirve para diagnosticar «cortes» en grabación
/// multicanal: muestra de un vistazo QUÉ canal se queda atrás y si el sistema (disco/CPU/GPU) no da abasto con
/// N canales a la vez. Solo LEE el estado de cada canal (no toca el hot-path de captura). Silencioso mientras
/// ningún canal graba; en cuanto detecta frames perdidos o caída de fps sube el log a WARNING.
/// </summary>
public sealed class ChannelHealthMonitor : BackgroundService
{
    private readonly IChannelManager _channels;
    private readonly ILogger<ChannelHealthMonitor> _log;
    private readonly Dictionary<Guid, long> _lastDrops = new(); // frames perdidos acumulados por canal (para el delta)
    private readonly Dictionary<string, long> _lastBytes = new(StringComparer.Ordinal); // bytes en disco por carpeta de canal
    private int _healthyTicks;

    /// <summary>Carpeta raíz de grabaciones (…/recordings). Cada canal escribe en la subcarpeta con su Key. Se
    /// usa para medir la escritura REAL al disco (crecimiento de los archivos), NO el bitrate de FFmpeg: en el
    /// pipeline unificado ese bitrate incluye el preview crudo (~180 Mbps a un socket) que no toca el disco.</summary>
    public string RecordingsRoot { get; init; } = "";

    /// <summary>Cadencia de muestreo. Cada muestra produce como mucho una línea de log.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Estando todo sano, solo se registra 1 de cada N muestras (evita llenar el log en 24/7). Con
    /// estrés (frames perdidos / fps bajo) se registra SIEMPRE, en WARNING.</summary>
    public int HealthyLogEvery { get; init; } = 4; // ~cada 60 s si todo va bien

    public ChannelHealthMonitor(IChannelManager channels, ILogger<ChannelHealthMonitor> log)
    {
        _channels = channels;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try { Sample(); }
                catch (Exception ex) { _log.LogDebug(ex, "Salud de canales: fallo al muestrear (se reintenta)."); }
            }
        }
        catch (OperationCanceledException) { /* parada del host */ }
    }

    private void Sample()
    {
        var recording = _channels.Channels
            .Select(c => c.Status)
            .Where(s => s.RecordingState is RecordingState.Recording or RecordingState.Paused)
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        if (recording.Count == 0) { _healthyTicks = 0; return; } // nada grabando → sin ruido en el log

        // La escritura al disco es un DELTA entre dos muestras: si algún canal se acaba de ver grabando, esta
        // muestra solo fija su línea base (crecimiento 0). En ese caso se primen las bases y NO se registra la
        // línea (sería un 0 engañoso); la siguiente muestra ya trae el ritmo real.
        bool warmup = recording.Any(s => !_lastBytes.ContainsKey(s.Key));

        double seconds = Math.Max(1, Interval.TotalSeconds);
        double totalDiskMBs = 0;
        bool anyStress = false;
        var parts = new List<string>(recording.Count);
        foreach (var s in recording)
        {
            var st = s.Stats;
            // Frames perdidos EN ESTE INTERVALO (delta del acumulado). Un reinicio de grabación resetea el
            // acumulado a 0 → Max(0, …) evita un delta negativo espurio.
            long prevDrop = _lastDrops.TryGetValue(s.ChannelId, out var pd) ? pd : st.DroppedFrames;
            long dropDelta = Math.Max(0, st.DroppedFrames - prevDrop);
            _lastDrops[s.ChannelId] = st.DroppedFrames;

            // Escritura REAL al disco = crecimiento de los archivos de la carpeta del canal en el intervalo (NO
            // el bitrate de FFmpeg, que en el pipeline unificado incluye el preview crudo ~180 Mbps a un socket).
            // La 1ª muestra solo fija la línea base (delta 0).
            long nowBytes = FolderBytes(Path.Combine(RecordingsRoot, s.Key));
            long prevBytes = _lastBytes.TryGetValue(s.Key, out var pb) ? pb : nowBytes;
            double diskMBs = Math.Max(0, nowBytes - prevBytes) / seconds / 1_000_000.0;
            _lastBytes[s.Key] = nowBytes;
            totalDiskMBs += diskMBs;

            double target = s.Signal.FrameRate?.Value ?? 0;                    // tasa de la fuente (objetivo)
            bool behind = target > 0 && st.OutputFps > 0 && st.OutputFps < target * 0.9; // >10% por debajo → se atrasa
            bool stress = dropDelta > 0 || behind;
            if (stress) anyStress = true;

            string fps = target > 0 ? $"{st.OutputFps:0}/{target:0}fps" : $"{st.OutputFps:0}fps";
            parts.Add($"{s.Key}:{fps} drop+{dropDelta} disco {diskMBs:0.0}MB/s{(stress ? " ⚠" : "")}");
        }

        if (warmup) return; // solo se fijaron las líneas base; se registra a partir de la próxima muestra

        string line = $"Salud grabación · {recording.Count} canal(es) · disco≈{totalDiskMBs:0.0} MB/s · " + string.Join(" | ", parts);

        if (anyStress)
        {
            _healthyTicks = 0;
            _log.LogWarning("{Line}  ← frames perdidos o fps por debajo del objetivo: el disco/CPU/GPU no da abasto para todos los canales.", line);
        }
        else if (_healthyTicks++ % Math.Max(1, HealthyLogEvery) == 0)
        {
            _log.LogInformation("{Line}", line);
        }
    }

    /// <summary>Suma el tamaño de los archivos de la carpeta de un canal (para medir su crecimiento en disco).
    /// Best-effort: un archivo en escritura o borrado (retención) se ignora sin romper la muestra.</summary>
    private static long FolderBytes(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
        long sum = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                try { sum += new FileInfo(f).Length; } catch { /* archivo en uso/borrado: se ignora */ }
            }
        }
        catch { /* carpeta inaccesible momentáneamente */ }
        return sum;
    }
}
