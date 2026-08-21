using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Lanza y supervisa un proceso FFmpeg. Núcleo de la resiliencia 24/7:
///  - reinicia con backoff exponencial si el proceso muere de forma inesperada;
///  - watchdog que mata y reinicia si no hay progreso durante <see cref="StallTimeout"/>;
///  - expone stdout (progress) y stderr (logs) como flujos de eventos.
/// </summary>
public sealed class FfmpegProcessSupervisor : IAsyncDisposable
{
    private readonly string _ffmpegPath;
    private readonly ILogger _log;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private DateTimeOffset _lastProgress;
    private int _restartCount;
    private volatile bool _closing;   // true durante el cierre ordenado: el watchdog NO debe matar entonces.
    private FileGrowthTracker? _growth; // #55: vigila que el ARCHIVO crezca (solo grabación, si hay sonda).

    public FfmpegProcessSupervisor(string ffmpegPath, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>#55: sonda que devuelve los bytes de la grabación en disco (negativo = no evaluable, p. ej. pausa).
    /// Si se define (solo grabación) y el archivo no crece en <see cref="FileStallTimeout"/> AUNQUE FFmpeg reporte
    /// progreso, el watchdog lo trata como estancado y reinicia en una pieza nueva. Red de seguridad para un
    /// encoder que emite frames vacíos o una escritura bloqueada, que el watchdog de progreso no vería. (#55.)</summary>
    public Func<long>? RecordedBytesProbe { get; init; }
    /// <summary>Margen sin crecimiento del archivo antes de considerar la grabación estancada. (Auditoría #55.)</summary>
    public TimeSpan FileStallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Espera máxima al cierre ordenado (flush/cierre del contenedor) antes de forzar el cierre.</summary>
    public TimeSpan GracefulTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// <c>true</c> (por defecto): al detener envía «q» y espera a que FFmpeg finalice el contenedor del archivo
    /// (imprescindible en GRABACIÓN para no corromper el MP4). <c>false</c>: proceso de solo-PREVIEW (sin archivo
    /// que finalizar) → se mata de inmediato al detener, SIN esperar la «q». Así, cambiar de entrada no se cuelga
    /// hasta <see cref="GracefulTimeout"/> cuando FFmpeg está atascado en la lectura del driver (p. ej. una DeckLink
    /// sin señal, que ignora la «q»). Seguro: no hay contenedor que cerrar en el preview.
    /// </summary>
    public bool FinalizeOnStop { get; init; } = true;
    /// <summary>
    /// <c>true</c> (por defecto): ante una salida INESPERADA (código ≠ 0 o kill del watchdog) relanza el mismo
    /// proceso con backoff (auto-recuperación 24/7 del PREVIEW —sin archivo— y de la carta de ajuste —bars
    /// generadas—). <c>false</c>: NO relanza —reabrir el mismo archivo de grabación con <c>-y</c> lo TRUNCARÍA—;
    /// en su lugar emite <see cref="Crashed"/> para que el dueño (el motor) reconstruya en una PIEZA NUEVA. (N1.)
    /// </summary>
    public bool RestartInternally { get; init; } = true;

    /// <summary>Código sintético devuelto cuando FFmpeg NO llegó a lanzarse (≠ 0 → reinicio/aviso). (N5.)</summary>
    private const int LaunchFailedCode = -100;
    public int MaxRestarts { get; init; } = int.MaxValue; // 24/7: reintentar indefinidamente
    /// <summary>Si el proceso estuvo activo al menos esto antes de morir, se trata como incidente AISLADO (no un
    /// crash-loop): se resetea el contador de reintentos para que el backoff no se quede clavado en el tope de
    /// 30 s por hiccups esporádicos repartidos a lo largo de semanas de 24/7. (Auditoría N24.)</summary>
    public TimeSpan HealthyResetAfter { get; init; } = TimeSpan.FromSeconds(60);

    public event EventHandler<string>? ProgressLine;   // stdout (key=value)
    public event EventHandler<string>? LogLine;        // stderr
    public event EventHandler<int>? Exited;            // código de salida de cada proceso
    public event EventHandler<int>? Restarted;         // nº de reinicio
    public event EventHandler<int>? Completed;         // fin definitivo (0=normal/stop, -1=agotó reintentos)
    /// <summary>El proceso murió inesperadamente y <see cref="RestartInternally"/> es <c>false</c>: el dueño (el
    /// motor de grabación) debe reiniciar en una PIEZA NUEVA sin reabrir el mismo archivo. Lleva el código de salida.</summary>
    public event EventHandler<int>? Crashed;

    /// <summary>Arranca el proceso y la supervisión. No bloquea.</summary>
    public Task StartAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _growth = RecordedBytesProbe is not null ? new FileGrowthTracker(DateTimeOffset.UtcNow) : null; // #55
        _runLoop = RunWithRestartAsync(arguments, _cts.Token);
        _ = WatchdogAsync(arguments, _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Completa cuando el proceso terminó DEFINITIVAMENTE (salida normal, caída sin reinicio interno o stop).
    /// A diferencia de esperar el evento <see cref="Completed"/>, sirve también cuando el final ya ocurrió
    /// ANTES de empezar a esperar: un evento pasado no se vuelve a disparar, esta tarea ya está completada.
    /// (Sin esto, detener un grabador que ya había muerto esperaba el timeout de drenaje entero.)
    /// </summary>
    public Task Finished => _runLoop ?? Task.CompletedTask;

    private async Task RunWithRestartAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _restartCount <= MaxRestarts)
        {
            long runStart = Stopwatch.GetTimestamp();
            int exitCode = await RunOnceAsync(arguments, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { Completed?.Invoke(this, 0); return; } // stop manual

            // Salida 0 = fin normal (EOF de una fuente finita o stop): NO reiniciar.
            if (exitCode == 0)
            {
                _log.LogInformation("FFmpeg finalizó normalmente (EOF/stop).");
                Completed?.Invoke(this, 0);
                return;
            }

            // Salida inesperada. Si el dueño gestiona el reinicio (grabación de la fuente: RestartInternally=false),
            // NO relanzar el mismo argv —reabriría el archivo con -y y lo TRUNCARÍA—: notifica y termina; el motor
            // reconstruye en una pieza nueva. El preview y la carta de ajuste sí se auto-relanzan aquí. (Auditoría N1.)
            if (!RestartInternally)
            {
                _log.LogWarning("FFmpeg salió con código {Code}; el reinicio lo gestiona el motor (pieza nueva, sin truncar).", exitCode);
                Crashed?.Invoke(this, exitCode);
                return;
            }

            // Incidente AISLADO (el proceso estuvo sano un buen rato antes de morir) → NO es un crash-loop:
            // resetea el contador para que el backoff vuelva a empezar corto, en vez de quedarse clavado en el
            // tope de 30 s por hiccups esporádicos repartidos en semanas de 24/7. (Auditoría N24.)
            if (Stopwatch.GetElapsedTime(runStart) >= HealthyResetAfter) _restartCount = 0;

            // Salida inesperada → backoff exponencial acotado (máx 30 s) y reintento.
            _restartCount++;
            var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, Math.Min(_restartCount, 6))));
            _log.LogWarning("FFmpeg salió con código {Code}. Reinicio #{N} en {Delay}.", exitCode, _restartCount, delay);
            Restarted?.Invoke(this, _restartCount);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { Completed?.Invoke(this, 0); return; }
        }
        Completed?.Invoke(this, -1); // agotó reintentos
    }

    private async Task<int> RunOnceAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // FFmpeg emite progreso (ASCII) y logs en UTF-8. Leerlo como UTF-8 deja los nombres con acentos
            // legibles en el log (p. ej. el dispositivo «Varios micrófonos») sin afectar al parseo ASCII.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _lastProgress = DateTimeOffset.UtcNow;
            ProgressLine?.Invoke(this, e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogLine?.Invoke(this, e.Data); };

        _lastProgress = DateTimeOffset.UtcNow;
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // No se pudo LANZAR FFmpeg (binario bloqueado por antivirus, ruta mala, handles agotados): antes esto
            // faultaba EN SILENCIO el bucle Y el watchdog, y el canal creía que grababa. Se registra y se devuelve
            // un código ≠ 0 para que RunWithRestartAsync reintente (preview) o avise por Crashed (grabación). (N5.)
            _log.LogError(ex, "No se pudo lanzar FFmpeg ({Path}); se reintentará/notificará.", _ffmpegPath);
            try { process.Dispose(); } catch { /* noop */ }
            return LaunchFailedCode;
        }
        // _process se asigna SOLO tras un Start correcto: así el watchdog nunca lee HasExited de un proceso sin
        // iniciar (que lanzaría) y sus comprobaciones son seguras. (N5.)
        _process = process;
        // Asocia el hijo a un Job Object KILL_ON_JOB_CLOSE: si la app muere de forma anormal (crash, kill,
        // fin de sesión), Windows mata este FFmpeg en vez de dejarlo grabando huérfano y reteniendo el
        // dispositivo/puerto de la fuente. (Auditoría 24/7, C2.)
        ChildProcessTracker.Track(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        try { await _process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await GracefulStopAsync().ConfigureAwait(false);
            return 0;
        }

        int code = _process.ExitCode;
        Exited?.Invoke(this, code);
        return code;
    }

    /// <summary>Mata el proceso si deja de reportar progreso (encoder colgado / pérdida de señal). En grabación
    /// intenta antes un cierre ordenado (q) para no dejar el archivo sin moov. Todo el cuerpo va protegido: una
    /// comprobación sobre un proceso en mal estado NO debe faultar el watchdog en silencio. (N5/N18.)</summary>
    private async Task WatchdogAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                var p = _process;
                if (_closing || p is null || p.HasExited) continue;

                var now = DateTimeOffset.UtcNow;
                bool progressStalled = now - _lastProgress > StallTimeout;
                // #55: aunque FFmpeg reporte progreso, si el ARCHIVO no crece durante FileStallTimeout la grabación
                // está muerta (encoder colgado emitiendo frames vacíos, escritura bloqueada, ruta que descarta en
                // silencio). Solo en grabación (hay archivo y sonda). Se evalúa SIEMPRE (no dentro del `||`) para no
                // perder muestras del crecimiento entre ticks.
                bool fileStalled = false;
                if (FinalizeOnStop && _growth is { } g && RecordedBytesProbe is { } probe)
                    fileStalled = g.IsStalled(SafeProbe(probe), now, FileStallTimeout);

                if (!progressStalled && !fileStalled) continue;

                string reason = progressStalled
                    ? $"sin progreso por {StallTimeout}"
                    : $"el archivo no crece desde hace {FileStallTimeout} (FFmpeg reporta progreso pero no escribe)";
                if (FinalizeOnStop)
                {
                    // Grabación: intenta un cierre ORDENADO (q) para que FFmpeg finalice el contenedor (moov) —
                    // clave con MP4 estándar, donde un kill abrupto deja la pieza SIN moov e ilegible—. Si no
                    // responde en unos segundos (colgado de verdad), fuerza el cierre. La muerte → recuperación en
                    // PIEZA NUEVA. (Auditoría N18/N1/#55.)
                    _log.LogError("Watchdog: {Reason}; intentando cierre ordenado (q) antes de forzar.", reason);
                    if (!await TryQuitAsync(p, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    {
                        _log.LogWarning("Watchdog: FFmpeg no respondió al cierre ordenado; forzando.");
                        try { p.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
                    }
                }
                else
                {
                    // Preview (sin archivo): matar de inmediato; el reinicio reconecta el preview.
                    _log.LogError("Watchdog: {Reason}. Forzando reinicio del preview.", reason);
                    try { p.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "Watchdog: comprobación falló (proceso en mal estado?)."); }
        }
    }

    /// <summary>Envía «q» por stdin y espera hasta <paramref name="wait"/> a que FFmpeg finalice el contenedor y
    /// salga. Devuelve true si salió (moov escrito); false si no se pudo escribir stdin o no salió a tiempo
    /// (colgado de verdad → el llamador fuerza el cierre). (N18.)</summary>
    private static async Task<bool> TryQuitAsync(Process p, TimeSpan wait)
    {
        try
        {
            await p.StandardInput.WriteAsync('q').ConfigureAwait(false);
            await p.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch { return false; }
        try
        {
            using var cts = new CancellationTokenSource(wait);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Lee la sonda de bytes con protección: un fallo devuelve -1 (no evaluable), nunca faulta el watchdog. (#55.)</summary>
    private static long SafeProbe(Func<long> probe)
    {
        try { return probe(); } catch { return -1; }
    }

    /// <summary>Cierre ordenado: envía 'q' por stdin para flush/finalizar contenedores.</summary>
    private async Task GracefulStopAsync()
    {
        if (_process is null || _process.HasExited) return;
        _closing = true; // el watchdog NO debe matar mientras FFmpeg finaliza/cierra el contenedor.

        // Solo-preview (sin archivo que finalizar): matar de inmediato en vez de esperar la «q». Evita que
        // cambiar de entrada se cuelgue hasta GracefulTimeout cuando FFmpeg está bloqueado en la lectura del
        // driver de una fuente sin señal (que no procesa la «q»). No hay contenedor que cerrar → es seguro.
        if (!FinalizeOnStop)
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            return;
        }

        try
        {
            await _process.StandardInput.WriteAsync('q').ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            // Espera generosa (hasta GracefulTimeout) a que FFmpeg cierre el contenedor; solo si se agota se
            // fuerza el cierre. Antes eran 5 s fijos: en grabaciones grandes o con varios canales cerrando a
            // la vez, el flush no llegaba a tiempo y se mataba a mitad → archivo corrupto.
            using var timeout = new CancellationTokenSource(GracefulTimeout);
            try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _log.LogWarning("FFmpeg no finalizó en {Timeout} tras 'q'; forzando cierre.", GracefulTimeout);
                try { _process.Kill(entireProcessTree: true); } catch { /* noop */ }
            }
        }
        catch { try { _process.Kill(entireProcessTree: true); } catch { /* noop */ } }
    }

    /// <summary>SOLO PARA TESTS: mata el proceso actual para simular una caída de FFmpeg y verificar que la
    /// recuperación NO trunca el archivo. No forma parte de la API de producción.</summary>
    internal bool KillCurrentProcessForTest()
    {
        var p = _process;
        if (p is null) return false;
        try { p.Kill(entireProcessTree: true); return true; } catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        // Cancela y espera a que el bucle termine: su ruta de cancelación ya hace el cierre ordenado
        // (envía 'q' y espera el flush del contenedor). Evita un GracefulStopAsync concurrente que
        // podría matar FFmpeg a mitad de la finalización del archivo (MP4 corrupto).
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_runLoop is not null)
        {
            try { await _runLoop.ConfigureAwait(false); }
            catch { /* el bucle no propaga; cierre ordenado garantizado */ }
        }
        else
        {
            await GracefulStopAsync().ConfigureAwait(false);
        }
        _process?.Dispose();
        _cts?.Dispose();
    }
}
