using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Infrastructure.Persistence;

namespace Baioss.Record.Infrastructure.Messaging;

/// <summary>
/// Persiste los eventos de dominio del bus en la tabla EventLog (auditoría 24/7). Antes el esquema, la
/// entidad, el índice por Timestamp y el repositorio existían pero <c>AppendAsync</c> NUNCA se invocaba: la
/// tabla quedaba vacía y no había trazabilidad tras un incidente nocturno (pérdida de señal, fallo de
/// encoder, disco lleno). Escribe EN LOTE (drena la cola y hace un solo <c>SaveChanges</c>) para no añadir
/// presión al único escritor de SQLite, y con cola acotada (DropOldest) para no crecer en RAM. (Auditoría #42.)
/// </summary>
public sealed class EventLogWriter : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IDbContextFactory<BaiossDbContext> _factory;
    private readonly ILogger<EventLogWriter> _log;
    private readonly Channel<EventLogEntry> _queue = System.Threading.Channels.Channel.CreateBounded<EventLogEntry>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>Días de auditoría a conservar en la tabla EventLog; las entradas más antiguas se podan
    /// periódicamente. ≤0 desactiva la poda (conservar siempre). Higiene de BD, no retención de medios. (N11.)</summary>
    public int RetentionDays { get; init; } = 30;

    public EventLogWriter(IEventBus bus, IDbContextFactory<BaiossDbContext> factory, ILogger<EventLogWriter> log)
    {
        _bus = bus;
        _factory = factory;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken ct)
        => Task.WhenAll(DrainAsync(ct), PruneLoopAsync(ct));

    /// <summary>Consume la cola del bus y escribe los eventos EN LOTE (un solo <c>SaveChanges</c> por drenado).</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        // Suscripción al bus: cada evento se ENCOLA (no se escribe en el hilo del publicador, que puede estar
        // en la ruta de start/stop de un canal). Un consumidor único drena y escribe en lote.
        using var sub = _bus.Subscribe<IDomainEvent>((e, _) => { _queue.Writer.TryWrite(ToEntry(e)); return Task.CompletedTask; });
        var batch = new List<EventLogEntry>(256);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 500 && _queue.Reader.TryRead(out var entry)) batch.Add(entry);
                if (batch.Count == 0) continue;
                try
                {
                    await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                    db.EventLog.AddRange(batch);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) { _log.LogError(ex, "EventLog: no se pudieron persistir {N} eventos.", batch.Count); }
            }
        }
        catch (OperationCanceledException) { /* parada del host */ }
    }

    /// <summary>
    /// PODA periódica de la tabla EventLog: borra (DELETE en bloque, sin cargar en memoria) las entradas más
    /// antiguas que <see cref="RetentionDays"/>, para que la tabla no crezca sin límite en 24/7 (miles de
    /// eventos/día). Es higiene de la BD de auditoría, INDEPENDIENTE de la retención de GRABACIONES (opt-in, que
    /// nunca borra medios aquí). <see cref="RetentionDays"/> ≤ 0 la desactiva (conservar siempre). (Auditoría N11.)
    /// </summary>
    private async Task PruneLoopAsync(CancellationToken ct)
    {
        if (RetentionDays <= 0) return;
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); } // deja componerse la BD antes de la 1ª pasada
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(RetentionDays);
                await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                int removed = await db.EventLog.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                if (removed > 0)
                    _log.LogInformation("EventLog: podadas {N} entradas anteriores a {Cutoff:yyyy-MM-dd} ({Days} días).", removed, cutoff, RetentionDays);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "EventLog: fallo al podar entradas antiguas (se reintenta en la próxima pasada)."); }

            try { await Task.Delay(TimeSpan.FromHours(6), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Mapea un evento de dominio a una entrada de auditoría. La categoría es el nombre del tipo, el
    /// mensaje su representación (los records muestran todas sus propiedades) y el canal se extrae por reflexión
    /// (la mayoría de eventos llevan ChannelId; SegmentCompleted/PerformanceDegraded no → null).</summary>
    private static EventLogEntry ToEntry(IDomainEvent e)
    {
        var t = e.GetType();
        return new EventLogEntry
        {
            Timestamp = e.OccurredAt,
            Category = t.Name,
            ChannelId = t.GetProperty("ChannelId")?.GetValue(e) as Guid?,
            Message = e.ToString() ?? t.Name,
            Severity = e switch
            {
                StorageEmergencyEntered => EventSeverity.Critical, // disco casi lleno: exige acción inmediata (Fase 3b)
                EncoderFailed => EventSeverity.Error,
                SignalLost or StorageLow or AudioSilenceDetected or AudioClippingDetected or PerformanceDegraded => EventSeverity.Warning,
                _ => EventSeverity.Info,
            },
        };
    }
}
