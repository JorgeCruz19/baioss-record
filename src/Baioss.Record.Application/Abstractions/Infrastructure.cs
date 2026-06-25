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
}

/// <summary>Resultado de sondear un archivo con ffprobe: qué pistas contiene y su duración.</summary>
public sealed record MediaProbe(bool HasVideo, bool HasAudio, double DurationSeconds, string? VideoCodec)
{
    /// <summary>Reproducible: tiene al menos una pista y duración positiva (no es un archivo vacío/corrupto/sin índice).</summary>
    public bool IsPlayable => (HasVideo || HasAudio) && DurationSeconds > 0;

    /// <summary>El archivo no se pudo leer (no existe, ffprobe falló o no halló pistas).</summary>
    public static readonly MediaProbe Unreadable = new(false, false, 0, null);
}
