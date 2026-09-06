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

    /// <summary>
    /// Suscripción al bus. Vive tanto como el servicio, NO solo mientras corre <see cref="ExecuteAsync"/>:
    /// el cierre de la aplicación para los servicios de fondo ANTES de detener las grabaciones en curso, así
    /// que los últimos eventos —justo los que dicen «esto se cortó porque se cerró el programa»— se publican
    /// cuando este servicio ya está parado. Si la suscripción muriera con el bucle, esa parte de la auditoría
    /// se perdería en silencio.
    /// </summary>
    private IDisposable? _subscription;

    /// <summary>Verdadero cuando el bucle ya no drena: a partir de ahí cada evento se escribe DIRECTAMENTE.</summary>
    private volatile bool _stopped;

    protected override Task ExecuteAsync(CancellationToken ct)
        => Task.WhenAll(DrainAsync(ct), PruneLoopAsync(ct));

    /// <summary>Consume la cola del bus y escribe los eventos EN LOTE (un solo <c>SaveChanges</c> por drenado).</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        // Suscripción al bus: mientras el servicio corre, cada evento se ENCOLA (no se escribe en el hilo del
        // publicador, que puede estar en la ruta de start/stop de un canal). Un consumidor único drena y
        // escribe en lote. Ya parado, se escribe en el acto (ver _stopped).
        _subscription = _bus.Subscribe<IDomainEvent>(async (e, token) =>
        {
            var entry = ToEntry(e);
            if (!_stopped && _queue.Writer.TryWrite(entry)) return;
            await WriteNowAsync(entry, token).ConfigureAwait(false);
        });
        var batch = new List<EventLogEntry>(256);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 500 && _queue.Reader.TryRead(out var entry)) batch.Add(entry);
                if (batch.Count == 0) continue;
                await PersistAsync(batch, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* parada del host */ }
    }

    /// <summary>
    /// Al parar el host: deja de encolar, VACÍA lo que quedara en la cola y mantiene viva la suscripción para
    /// que los eventos del cierre (las grabaciones que se detienen después) sigan quedando registrados.
    /// </summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        _stopped = true;
        await base.StopAsync(ct).ConfigureAwait(false);

        var pending = new List<EventLogEntry>();
        while (_queue.Reader.TryRead(out var entry)) pending.Add(entry);
        // CancellationToken.None: el token del cierre puede venir ya cancelado o vencer en mitad del vaciado,
        // y perder aquí la auditoría es peor que tardar unos milisegundos más en cerrar.
        if (pending.Count > 0) await PersistAsync(pending, CancellationToken.None).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    /// <summary>Escribe un único evento en el acto (ruta de cierre, cuando ya no hay bucle que drene).</summary>
    private Task WriteNowAsync(EventLogEntry entry, CancellationToken ct)
        => PersistAsync(new[] { entry }, ct);

    private async Task PersistAsync(IReadOnlyCollection<EventLogEntry> batch, CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.EventLog.AddRange(batch);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogError(ex, "EventLog: no se pudieron persistir {N} eventos.", batch.Count); }
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
    /// mensaje su representación (los records muestran todas sus propiedades) y el canal y el operador se
    /// extraen por reflexión (no todos los eventos los llevan → null).</summary>
    internal static EventLogEntry ToEntry(IDomainEvent e)
    {
        var t = e.GetType();
        return new EventLogEntry
        {
            Timestamp = e.OccurredAt,
            Category = t.Name,
            ChannelId = t.GetProperty("ChannelId")?.GetValue(e) as Guid?,
            // Operator: la columna existía desde el principio pero nadie la rellenaba, así que la auditoría no
            // sabía DE QUIÉN era cada grabación aunque el evento lo trajera. Se extrae igual que el canal.
            Operator = t.GetProperty("Operator")?.GetValue(e) as string,
            Message = e.ToString() ?? t.Name,
            PayloadJson = ToJson(e, t),
            Severity = e switch
            {
                StorageEmergencyEntered => EventSeverity.Critical, // disco casi lleno: exige acción inmediata (Fase 3b)
                EncoderFailed or RecordingStartFailed => EventSeverity.Error,
                // Una grabación que se corta sola NO es rutina: se marca como aviso para que destaque entre las
                // paradas normales al revisar la auditoría de una noche.
                RecordingStopped { Reason: RecordingStopReason.DiskFull or RecordingStopReason.Error } => EventSeverity.Warning,
                ScheduledRecordingSkipped or OrphanSessionsClosed => EventSeverity.Warning,
                SignalLost or StorageLow or AudioSilenceDetected or AudioClippingDetected or PerformanceDegraded => EventSeverity.Warning,
                _ => EventSeverity.Info,
            },
        };
    }

    /// <summary>
    /// Copia estructurada del evento, para poder explotar la auditoría con herramientas en vez de leyendo
    /// texto. La columna existía y nunca se rellenaba. Best-effort: si un evento no fuese serializable, la
    /// entrada se guarda igual con su mensaje —perder el JSON es aceptable; perder la entrada, no—.
    /// </summary>
    private static string? ToJson(IDomainEvent e, Type t)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(e, t); }
        catch { return null; }
    }
}
