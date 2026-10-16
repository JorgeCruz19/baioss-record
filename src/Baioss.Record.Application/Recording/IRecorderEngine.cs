using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Application.Recording;

/// <summary>Telemetría en vivo del encoder, una muestra por segundo aprox.</summary>
/// <param name="InputFps">El <c>fps</c> que imprime FFmpeg: la MEDIA desde que arrancó el proceso.</param>
/// <param name="OutputFps">Ritmo de AHORA (frames por segundo de pared en los últimos segundos, entre bloques de progreso):
/// el «FPS OUT» del panel. La media de FFmpeg tardaba un minuto en olvidar un atraso ya vaciado.</param>
/// <param name="DroppedFrames"><c>drop_frames</c> acumulado del proceso actual.</param>
/// <param name="DuplicatedFrames"><c>dup_frames</c> acumulado del proceso actual.</param>
/// <param name="Bitrate">Bitrate de la salida (al grabar, el real del archivo medido en disco).</param>
/// <param name="BufferHealth">Salud del búfer, 0..1.</param>
/// <param name="Timecode">Tiempo de la salida (del <c>out_time</c> de FFmpeg).</param>
/// <param name="FrameCount">El <c>frame</c> ACUMULADO del proceso actual (base de la alarma de frames perdidos).</param>
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
    /// <summary>Bytes escritos al disco por la sesión de grabación EN CURSO (segmentos + archivo activo). El
    /// motor lo mide ACOTADO a la sesión (no recorre la carpeta del canal); el monitor de salud lo usa para el
    /// ritmo de escritura sin escanear O(nº archivos) toda la carpeta. 0 fuera de grabación. (Auditoría N16.)</summary>
    public long RecordedBytes { get; init; }

    /// <summary>Tiempo de medios que el proceso ya procesó (<c>out_time</c> de FFmpeg, en µs); -1 mientras FFmpeg no lo sabe
    /// («N/A» al arrancar). Frente al reloj de pared dice si el proceso va al día: lo usa la alarma de frames perdidos.</summary>
    public long MediaTimeUs { get; init; } = -1;

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
