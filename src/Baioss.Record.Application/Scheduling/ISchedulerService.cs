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
}
