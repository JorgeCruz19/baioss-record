using Baioss.Record.Domain.Events;

namespace Baioss.Record.Application.Abstractions;

/// <summary>Reloj inyectable para testabilidad (evita DateTimeOffset.UtcNow directo).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Bus de eventos interno. Alimenta el WebSocket de la API, la auditoría y la UI.
/// Implementación in-process (canal/observable); pub/sub desacopla productores y consumidores.
/// </summary>
public interface IEventBus
{
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct = default);
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IDomainEvent;
}

/// <summary>Localiza el binario de FFmpeg/FFprobe y valida los encoders disponibles.</summary>
public interface IFfmpegLocator
{
    string FfmpegPath { get; }
    string FfprobePath { get; }
    Task<IReadOnlyCollection<string>> GetAvailableEncodersAsync(CancellationToken ct = default);

    /// <summary>Sondea un archivo con ffprobe (qué pistas tiene y su duración) para verificar la integridad de una grabación recién cerrada.</summary>
    Task<MediaProbe> ProbeMediaAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Reescribe un MP4/MOV (SIN recodificar, <c>-c copy</c>) a la forma estándar con el índice (<c>moov</c>) al
    /// INICIO (<c>+faststart</c>): convierte el fMP4 fragmentado —robusto ante cortes pero con seek por estimación
    /// en algunos reproductores como VLC, sobre todo en archivos grandes— en un MP4 con tabla de muestras y de
    /// keyframes completa al principio, que permite buscar (scrubbing) de forma instantánea y precisa. Atómico:
    /// escribe a un temporal y solo entonces sustituye al original; si algo falla, conserva el original intacto.
    /// Devuelve true si reescribió el archivo. No-op (false) para contenedores que no sean MP4/MOV.
    /// </summary>
    Task<bool> RemuxFaststartAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Tamaño máximo (bytes) para optimizar la búsqueda con faststart al detener una grabación de archivo único.
    /// Por ENCIMA no se reescribe el archivo: el remux copia el archivo entero (lectura + escritura completas) y,
    /// con archivos grandes (varios GB), satura el disco y compite con las grabaciones activas (que podrían perder
    /// frames). El fMP4 omitido sigue siendo válido y reproducible (solo con seek por estimación). 0 = sin límite.
    /// Configurable («Recording:FaststartMaxGB»). Para archivos largos con búsqueda óptima: contenedor MKV o segmentación.
    /// </summary>
    long FaststartMaxBytes { get; }
}

/// <summary>Resultado de sondear un archivo con ffprobe: qué pistas contiene y su duración.</summary>
public sealed record MediaProbe(bool HasVideo, bool HasAudio, double DurationSeconds, string? VideoCodec)
{
    /// <summary>Reproducible: tiene al menos una pista y duración positiva (no es un archivo vacío/corrupto/sin índice).</summary>
    public bool IsPlayable => (HasVideo || HasAudio) && DurationSeconds > 0;

    /// <summary>El archivo no se pudo leer (no existe, ffprobe falló o no halló pistas).</summary>
    public static readonly MediaProbe Unreadable = new(false, false, 0, null);
}
