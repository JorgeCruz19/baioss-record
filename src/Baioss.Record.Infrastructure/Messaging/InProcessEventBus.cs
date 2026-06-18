using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Domain.Events;

namespace Baioss.Record.Infrastructure.Messaging;

/// <summary>
/// Bus de eventos in-process (pub/sub) que desacopla productores (canales, monitor de
/// señal, storage) de consumidores (UI, auditoría, WebSocket de la API). Los handlers se
/// invocan en serie; una excepción en un subscriptor se registra y no afecta al resto.
/// </summary>
public sealed class InProcessEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly ILogger<InProcessEventBus>? _log;

    public InProcessEventBus(ILogger<InProcessEventBus>? log = null) => _log = log;

    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct = default)
    {
        foreach (var sub in _subscriptions.Values)
        {
            if (!sub.EventType.IsInstanceOfType(domainEvent)) continue;
            try
            {
                await sub.Handler(domainEvent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Subscriptor del evento {Event} lanzó una excepción.", domainEvent.GetType().Name);
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IDomainEvent
    {
        var id = Guid.NewGuid();
        _subscriptions[id] = new Subscription(typeof(TEvent), (e, ct) => handler((TEvent)e, ct));
        return new Unsubscriber(() => _subscriptions.TryRemove(id, out _));
    }

    private sealed record Subscription(Type EventType, Func<IDomainEvent, CancellationToken, Task> Handler);

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
