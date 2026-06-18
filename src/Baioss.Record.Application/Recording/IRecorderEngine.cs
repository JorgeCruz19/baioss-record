using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Application.Recording;

/// <summary>Telemetría en vivo del encoder, una muestra por segundo aprox.</summary>
public sealed record RecorderStats(
    double InputFps,
    double OutputFps,
    long DroppedFrames,
    long DuplicatedFrames,
    Bitrate Bitrate,
    double BufferHealth,   // 0..1
    Timecode Timecode,
    long FrameCount)
{
    public static readonly RecorderStats Empty =
        new(0, 0, 0, 0, new Bitrate(0), 1, Timecode.Zero, 0);
}

/// <summary>
/// Motor de grabación de un canal. Una instancia controla un proceso FFmpeg que
/// codifica, segmenta, genera proxy y hace streaming simultáneo (muxer tee).
/// </summary>
public interface IRecorderEngine : IAsyncDisposable
{
    RecordingState State { get; }
    RecorderStats Stats { get; }

    event EventHandler<RecordingState>? StateChanged;
    event EventHandler<RecorderStats>? StatsUpdated;

    /// <summary>Notifica cada vez que se cierra un archivo de segmento (continuidad/índice).</summary>
    event EventHandler<Segment>? SegmentClosed;

    Task StartAsync(RecordingSession session, RecordingProfile profile, ICaptureSource source, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
}

/// <summary>
/// Traduce una <see cref="SegmentationPolicy"/> a argumentos FFmpeg y mantiene el
/// índice de segmentos (continuidad, auto-merge, export de timeline).
/// </summary>
public interface ISegmenter
{
    IReadOnlyList<string> BuildSegmentArguments(SegmentationPolicy policy, string outputDirectory, string pattern);
    Task<string> ExportTimelineAsync(Guid sessionId, CancellationToken ct = default);
    Task<string> AutoMergeAsync(Guid sessionId, string outputPath, CancellationToken ct = default);
}

/// <summary>Captura imágenes fijas durante la grabación (manual, programada, thumbnail).</summary>
public interface ISnapshotService
{
    Task<string> CaptureAsync(Guid channelId, string format, CancellationToken ct = default);
    Task StartAutoThumbnailsAsync(Guid sessionId, TimeSpan interval, CancellationToken ct = default);
}

/// <summary>Genera proxy(s) H.264/H.265 en paralelo a la grabación principal.</summary>
public interface IProxyGenerator
{
    IReadOnlyList<string> BuildProxyArguments(ProxyProfile proxy, string outputPath);
}
