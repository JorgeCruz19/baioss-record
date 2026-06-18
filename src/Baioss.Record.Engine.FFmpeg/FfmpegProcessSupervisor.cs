using System.Diagnostics;
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
    private DateTimeOffset _lastProgress;
    private int _restartCount;

    public FfmpegProcessSupervisor(string ffmpegPath, ILogger log)
    {
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(10);
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
        _ = RunWithRestartAsync(arguments, _cts.Token);
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

            if (_process is { HasExited: false } &&
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
        try
        {
            await _process.StandardInput.WriteAsync('q').ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
        }
        catch { try { _process.Kill(entireProcessTree: true); } catch { /* noop */ } }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        await GracefulStopAsync().ConfigureAwait(false);
        _process?.Dispose();
        _cts?.Dispose();
    }
}
