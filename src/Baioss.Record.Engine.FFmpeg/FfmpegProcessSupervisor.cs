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

    public FfmpegProcessSupervisor(string ffmpegPath, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Espera máxima al cierre ordenado (flush/cierre del contenedor) antes de forzar el cierre.</summary>
    public TimeSpan GracefulTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRestarts { get; init; } = int.MaxValue; // 24/7: reintentar indefinidamente

    public event EventHandler<string>? ProgressLine;   // stdout (key=value)
    public event EventHandler<string>? LogLine;        // stderr
    public event EventHandler<int>? Exited;            // código de salida de cada proceso
    public event EventHandler<int>? Restarted;         // nº de reinicio
    public event EventHandler<int>? Completed;         // fin definitivo (0=normal/stop, -1=agotó reintentos)

    /// <summary>Arranca el proceso y la supervisión. No bloquea.</summary>
    public Task StartAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runLoop = RunWithRestartAsync(arguments, _cts.Token);
        _ = WatchdogAsync(arguments, _cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunWithRestartAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _restartCount <= MaxRestarts)
        {
            int exitCode = await RunOnceAsync(arguments, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { Completed?.Invoke(this, 0); return; } // stop manual

            // Salida 0 = fin normal (EOF de una fuente finita o stop): NO reiniciar.
            if (exitCode == 0)
            {
                _log.LogInformation("FFmpeg finalizó normalmente (EOF/stop).");
                Completed?.Invoke(this, 0);
                return;
            }

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

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _lastProgress = DateTimeOffset.UtcNow;
            ProgressLine?.Invoke(this, e.Data);
        };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogLine?.Invoke(this, e.Data); };

        _lastProgress = DateTimeOffset.UtcNow;
        _process.Start();
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

    /// <summary>Mata el proceso si deja de reportar progreso (encoder colgado / pérdida de señal).</summary>
    private async Task WatchdogAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (!_closing && _process is { HasExited: false } &&
                DateTimeOffset.UtcNow - _lastProgress > StallTimeout)
            {
                _log.LogError("Watchdog: sin progreso por {Timeout}. Forzando reinicio.", StallTimeout);
                try { _process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            }
        }
    }

    /// <summary>Cierre ordenado: envía 'q' por stdin para flush/finalizar contenedores.</summary>
    private async Task GracefulStopAsync()
    {
        if (_process is null || _process.HasExited) return;
        _closing = true; // el watchdog NO debe matar mientras FFmpeg finaliza/cierra el contenedor.
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
