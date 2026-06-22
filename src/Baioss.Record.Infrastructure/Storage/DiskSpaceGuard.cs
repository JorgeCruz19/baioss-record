using System.IO;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>Severidad del espacio en disco para la grabación en curso.</summary>
public enum DiskLevel { Ok, Low, Critical }

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

    public DiskSpaceGuard(ILogger log) => _log = log;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Aviso (amarillo) cuando el tiempo restante estimado baja de aquí.</summary>
    public TimeSpan WarnRemaining { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Crítico (rojo / auto-stop) cuando el tiempo restante baja de aquí.</summary>
    public TimeSpan CriticalRemaining { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Piso absoluto de espacio libre: por debajo es crítico aunque el ritmo de datos sea bajo.</summary>
    public long MinFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024; // 2 GiB

    /// <summary>Estado actual del disco (nivel + libres + restante estimado). Útil para la UI continua.</summary>
    public event EventHandler<(DiskLevel Level, StorageInfo Info)>? Updated;

    public void Start(Func<string?> outputDir, Func<long> bytesPerSecond)
    {
        _outputDir = outputDir;
        _bytesPerSecond = bytesPerSecond;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { /* cancelación */ } }
        _cts.Dispose();
        _cts = null; _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (free, total) = ReadDrive(_outputDir());
                var (level, info) = Evaluate(free, total, _bytesPerSecond(), WarnRemaining, CriticalRemaining, MinFreeBytes);
                Updated?.Invoke(this, (level, info));
            }
            catch (Exception ex) { _log.LogDebug(ex, "Guarda de disco: fallo al medir el espacio."); }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private (long Free, long Total) ReadDrive(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return (0, 0);
        var root = Path.GetPathRoot(Path.GetFullPath(dir));
        if (string.IsNullOrEmpty(root)) return (0, 0);
        var d = new DriveInfo(root);
        return d.IsReady ? (d.AvailableFreeSpace, d.TotalSize) : (0, 0);
    }

    /// <summary>
    /// Decisión pura (testeable, sin disco): combina el piso de bytes libres y el tiempo restante
    /// estimado (libres ÷ ritmo de datos) para clasificar en Ok/Bajo/Crítico. Si no hay ritmo de datos
    /// (aún sin telemetría), solo aplica el piso de bytes.
    /// </summary>
    public static (DiskLevel Level, StorageInfo Info) Evaluate(
        long freeBytes, long totalBytes, long bytesPerSecond,
        TimeSpan warn, TimeSpan critical, long minFreeBytes)
    {
        TimeSpan? remaining = bytesPerSecond > 0
            ? TimeSpan.FromSeconds((double)freeBytes / bytesPerSecond)
            : null;
        var info = new StorageInfo(freeBytes, totalBytes, remaining);

        if (freeBytes <= minFreeBytes) return (DiskLevel.Critical, info);
        if (remaining is { } rc && rc <= critical) return (DiskLevel.Critical, info);
        if (freeBytes <= minFreeBytes * 2) return (DiskLevel.Low, info);
        if (remaining is { } rw && rw <= warn) return (DiskLevel.Low, info);
        return (DiskLevel.Ok, info);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
