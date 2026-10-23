using Baioss.Record.Domain;

namespace Baioss.Record.Application.Clips;

/// <summary>Con qué codificador de vídeo se recodifica un clip en el modo exacto. Se recibe por la API: NO reordenar.</summary>
public enum ClipVideoEncoder
{
    /// <summary>El de la grabación: su mismo códec (H.264, HEVC, ProRes, DNxHD/HR, MPEG-2) con los parámetros de su preset.</summary>
    SameAsRecording = 0,
    /// <summary>H.264 (NVENC si la tarjeta lo tiene; si no, x264).</summary>
    H264 = 1,
    /// <summary>HEVC / H.265 (NVENC si la tarjeta lo tiene; si no, x265).</summary>
    Hevc = 2,
    /// <summary>Apple ProRes 422 HQ en MOV, con el audio en PCM: para llevar el clip a edición.</summary>
    ProRes = 3,
}

/// <summary>Lo que se pide: el tramo [<see cref="InFrame"/>, <see cref="OutFrame"/>) de la grabación <see cref="SessionId"/>
/// (cuadros desde su principio; <see cref="OutFrame"/> no entra) y cómo exportarlo.</summary>
public sealed record ClipRequest(Guid SessionId, long InFrame, long OutFrame, ClipMode Mode)
{
    public ClipVideoEncoder Encoder { get; init; } = ClipVideoEncoder.SameAsRecording;
    /// <summary>Nombre del archivo (sin extensión); null = uno legible a partir de la grabación y el tramo.</summary>
    public string? Name { get; init; }
    /// <summary>Quién lo exporta (auditoría).</summary>
    public string? Operator { get; init; }
}

/// <summary>El clip que quedó en la carpeta de clips.</summary>
public sealed record ClipResult(string FilePath, long InFrame, long OutFrame, TimeSpan In, TimeSpan Out, ClipMode Mode, long SizeBytes)
{
    /// <summary>Codificador de vídeo del modo exacto (p. ej. «h264_nvenc»); null en el rápido, que copia.</summary>
    public string? VideoEncoder { get; init; }

    /// <summary>Rápido: cuánto antes de la entrada empieza el archivo (desde el fotograma clave anterior). Lo ven solo los
    /// reproductores que no respetan la lista de edición (el de Windows, por ejemplo).</summary>
    public TimeSpan PreRoll { get; init; }

    /// <summary>Rápido con una grabación con B-frames (x264/x265/QuickSync): cuadros que quedan al final después de la salida
    /// (la copia corta en orden de decodificación; se toman los justos para que no falte ninguno).</summary>
    public int ExtraFrames { get; init; }

    public TimeSpan Duration => Out - In;
}

/// <summary>Por qué no se pudo exportar un clip.</summary>
public enum ClipError
{
    /// <summary>La grabación no existe (¿la borraron?).</summary>
    NotFound,
    /// <summary>Se está grabando: no se puede cortar hasta que termine.</summary>
    InProgress,
    /// <summary>No tiene archivos registrados.</summary>
    NoFiles,
    /// <summary>Falta en el disco un archivo de la grabación (<see cref="ClipException.Detail"/> dice cuál).</summary>
    FileMissing,
    /// <summary>FFmpeg no sabe leer un archivo de la grabación (dañado, sin índice…).</summary>
    Unreadable,
    /// <summary>El tramo no cabe en la grabación.</summary>
    OutOfRange,
    /// <summary>El tramo está vacío (la salida no va después de la entrada).</summary>
    Empty,
    /// <summary>El tramo cruza piezas con parámetros distintos (otro tamaño o códec tras un cambio de formato de la señal): no
    /// se pueden unir con el demuxer concat.</summary>
    PiecesDiffer,
    /// <summary>FFmpeg falló al cortar (<see cref="ClipException.Detail"/> lleva lo que dijo).</summary>
    FfmpegFailed,
    /// <summary>No se pudo escribir en la carpeta de clips.</summary>
    OutputFailed,
    /// <summary>No hay FFmpeg (modo demostración).</summary>
    Unavailable,
}

/// <summary>Un clip que no se pudo exportar: el motivo (<see cref="Error"/>) y, para el registro y el detalle, <see cref="Detail"/>.</summary>
public sealed class ClipException : Exception
{
    public ClipException(ClipError error, string? detail = null, Exception? inner = null)
        : base(detail is null ? error.ToString() : $"{error}: {detail}", inner)
    {
        Error = error;
        Detail = detail;
    }

    public ClipError Error { get; }
    public string? Detail { get; }
}

/// <summary>
/// Revisión de grabaciones TERMINADAS y exportación de clips (ventana «Revisar» y <c>POST /api/v1/recordings/{id}/clip</c>).
/// No toca cómo se graba: lee los archivos de la grabación (cada segmento o pieza es un <c>Segment</c>) y escribe el clip en la
/// carpeta de clips, con una entrada «Clip exportado» en la auditoría.
/// </summary>
public interface IClipExportService
{
    /// <summary>Carpeta donde quedan los clips: la de «Almacenamiento» o, si no se eligió ninguna, la de por defecto.</summary>
    string ClipsFolder { get; }

    /// <summary>La carpeta de por defecto (<c>clips</c>, junto a los datos de la aplicación).</summary>
    string DefaultClipsFolder { get; }

    /// <summary>La línea de tiempo de la grabación (sus piezas, en cuadros, con lo que dice ffprobe de cada una). Lanza
    /// <see cref="ClipException"/> si no existe, se está grabando, le falta un archivo o FFmpeg no lo lee.</summary>
    Task<RecordingTimeline> LoadTimelineAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Exporta el clip. <paramref name="progress"/> recibe de 0 a 1. Lanza <see cref="ClipException"/> si no se pudo.</summary>
    Task<ClipResult> ExportAsync(ClipRequest request, IProgress<double>? progress = null, CancellationToken ct = default);
}
