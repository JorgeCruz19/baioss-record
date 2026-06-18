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
}
