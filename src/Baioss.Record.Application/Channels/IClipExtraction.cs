namespace Baioss.Record.Application.Channels;

/// <summary>
/// Implementado por los motores que pueden sacar un CLIP de los últimos segundos o minutos de la grabación EN CURSO,
/// sin detenerla ni recodificar: otro proceso lee el archivo que aún se está escribiendo y copia el tramo pedido a la
/// subcarpeta <c>clips/</c> del canal. Solo con contenedores legibles mientras crecen (MP4 fragmentado y TS; medido:
/// MXF no tiene índice hasta cerrarse y el MP4 estándar no tiene <c>moov</c> hasta cerrarse). El corte empieza en el
/// fotograma clave anterior al instante pedido (el clip sale un poco más largo por delante, nunca más corto) y termina
/// un instante antes del final del archivo (el último fragmento puede estar a medias).
/// </summary>
public interface IClipExtraction
{
    /// <summary>True cuando hay grabación en curso (o en pausa) en un contenedor que se puede leer mientras crece.</summary>
    bool CanExtractClip { get; }

    /// <summary>
    /// Copia los últimos <paramref name="lastSeconds"/> de la grabación en curso a un archivo nuevo y lo devuelve.
    /// <paramref name="operatorName"/> es quién lo pidió (auditoría). Lanza <see cref="ClipExtractionException"/> con el
    /// motivo si no se puede (sin grabación, contenedor no legible en caliente, otro clip en marcha, sin espacio, fallo).
    /// </summary>
    Task<ClipResult> ExtractClipAsync(TimeSpan lastSeconds, string? operatorName = null, CancellationToken ct = default);
}

/// <summary>El clip creado: ruta, duración real (algo mayor que la pedida: arranca en un fotograma clave) y tamaño.</summary>
public sealed record ClipResult(string FilePath, TimeSpan Duration, long SizeBytes, TimeSpan Requested);

public enum ClipError
{
    /// <summary>El canal no está grabando.</summary>
    NotRecording,
    /// <summary>El contenedor de la grabación no se puede leer mientras crece (MXF, MP4 estándar…).</summary>
    UnsupportedContainer,
    /// <summary>Ya hay un clip en marcha en este canal.</summary>
    Busy,
    /// <summary>La grabación aún no tiene material suficiente.</summary>
    TooShort,
    /// <summary>No hay espacio en el disco de destino.</summary>
    NoSpace,
    /// <summary>La duración pedida está fuera de rango.</summary>
    InvalidDuration,
    /// <summary>FFmpeg no pudo generar el clip o el resultado no se puede reproducir.</summary>
    Failed,
}

/// <summary>Por qué no se pudo extraer el clip: el mensaje va en el idioma de la aplicación; <see cref="Code"/> es estable
/// (inglés técnico) para que un cliente de la API pueda distinguirlo.</summary>
public sealed class ClipExtractionException(ClipError error, string message) : InvalidOperationException(message)
{
    public ClipError Error { get; } = error;

    public string Code => Error switch
    {
        ClipError.NotRecording => "not-recording",
        ClipError.UnsupportedContainer => "unsupported-container",
        ClipError.Busy => "busy",
        ClipError.TooShort => "too-short",
        ClipError.NoSpace => "no-space",
        ClipError.InvalidDuration => "invalid-duration",
        _ => "failed",
    };
}
