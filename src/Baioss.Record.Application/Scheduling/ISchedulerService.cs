using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Scheduling;

/// <summary>
/// Una grabación programada que el scheduler tiene EN MARCHA ahora mismo: qué tarea, en qué canal, desde cuándo y hasta
/// cuándo. Es la realidad (la inició esta instancia y sigue viva), no una deducción por la hora: una tarea saltada o que
/// no pudo arrancar no aparece aquí aunque «le toque».
/// </summary>
public sealed record ActiveScheduledRecording(
    Guid JobId, Guid ChannelId, Guid SessionId, string Title, DateTimeOffset StartedAt, DateTimeOffset EndsAt);

/// <summary>
/// Servicio de programación. Persiste trabajos y los dispara por fecha/hora/CRON,
/// invocando los casos de uso correspondientes (start/stop, cambio de perfil/fuente).
/// Corre como BackgroundService con tolerancia a derivas de reloj.
/// <para>Los cambios (<c>ScheduleAsync</c>, <c>UpdateAsync</c>, <c>CancelAsync</c>, <c>SetEnabledAsync</c>) llevan
/// <c>operatorName</c>: quién lo hizo, para la auditoría (<c>ScheduleChanged</c>). La programación se gestiona desde la
/// aplicación Y desde el panel web.</para>
/// </summary>
public interface ISchedulerService
{
    Task<ScheduledJob> ScheduleAsync(ScheduledJob job, string? operatorName = null, CancellationToken ct = default);
    Task CancelAsync(Guid jobId, string? operatorName = null, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledJob>> GetUpcomingAsync(DateTimeOffset until, CancellationToken ct = default);

    /// <summary>Todos los trabajos programados (para gestionarlos en la UI).</summary>
    Task<IReadOnlyList<ScheduledJob>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Activa o pausa un trabajo sin borrarlo.</summary>
    Task SetEnabledAsync(Guid jobId, bool enabled, string? operatorName = null, CancellationToken ct = default);

    /// <summary>Actualiza un trabajo existente (mismo Id) con una nueva configuración (hora, días, duración…).</summary>
    Task UpdateAsync(ScheduledJob job, string? operatorName = null, CancellationToken ct = default);

    /// <summary>Canales que ahora mismo ejecutan una grabación programada iniciada por el scheduler.</summary>
    IReadOnlySet<Guid> ActiveScheduledChannels { get; }

    /// <summary>Las grabaciones programadas en marcha, con su tarea y su hora de fin (para «tarea activa» del canal).</summary>
    IReadOnlyList<ActiveScheduledRecording> ActiveRecordings { get; }

    /// <summary>Se eleva cuando cambia el conjunto de grabaciones programadas activas (inicio/fin/salto).</summary>
    event EventHandler? ActiveChanged;

    /// <summary>Se eleva cuando cambia la LISTA de tareas (alta, edición, baja, pausa), venga de la aplicación o de la
    /// API: quien la esté mostrando la refresca sin esperar a su temporizador.</summary>
    event EventHandler? JobsChanged;

    /// <summary>
    /// Salta la grabación programada en curso de un canal: la detiene YA y marca SOLO esa ocurrencia como
    /// saltada (no se reanuda). Las siguientes ocurrencias (diaria/semanal) se ejecutan con normalidad.
    /// Devuelve false si ese canal no tenía ninguna grabación programada en marcha.
    /// </summary>
    Task<bool> SkipCurrentAsync(Guid channelId, CancellationToken ct = default);
}
