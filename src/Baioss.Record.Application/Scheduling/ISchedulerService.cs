using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Scheduling;

/// <summary>
/// Servicio de programación. Persiste trabajos y los dispara por fecha/hora/CRON,
/// invocando los casos de uso correspondientes (start/stop, cambio de perfil/fuente).
/// Corre como BackgroundService con tolerancia a derivas de reloj.
/// </summary>
public interface ISchedulerService
{
    Task<ScheduledJob> ScheduleAsync(ScheduledJob job, CancellationToken ct = default);
    Task CancelAsync(Guid jobId, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledJob>> GetUpcomingAsync(DateTimeOffset until, CancellationToken ct = default);

    /// <summary>Todos los trabajos programados (para gestionarlos en la UI).</summary>
    Task<IReadOnlyList<ScheduledJob>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Activa o pausa un trabajo sin borrarlo.</summary>
    Task SetEnabledAsync(Guid jobId, bool enabled, CancellationToken ct = default);

    /// <summary>Actualiza un trabajo existente (mismo Id) con una nueva configuración (hora, días, duración…).</summary>
    Task UpdateAsync(ScheduledJob job, CancellationToken ct = default);

    /// <summary>Canales que ahora mismo ejecutan una grabación programada iniciada por el scheduler.</summary>
    IReadOnlySet<Guid> ActiveScheduledChannels { get; }

    /// <summary>Se eleva cuando cambia el conjunto de grabaciones programadas activas (inicio/fin/salto).</summary>
    event EventHandler? ActiveChanged;

    /// <summary>
    /// Salta la grabación programada en curso de un canal: la detiene YA y marca SOLO esa ocurrencia como
    /// saltada (no se reanuda). Las siguientes ocurrencias (diaria/semanal) se ejecutan con normalidad.
    /// </summary>
    Task SkipCurrentAsync(Guid channelId, CancellationToken ct = default);
}
