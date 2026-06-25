using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Persistence;

/// <summary>Repositorio genérico de solo-CRUD para entidades con clave Guid.</summary>
public interface IRepository<T> where T : class
{
    Task<T?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> ListAsync(CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
}

public interface IChannelRepository : IRepository<Channel>;
public interface IInputSourceRepository : IRepository<InputSource>;
public interface IRecordingProfileRepository : IRepository<RecordingProfile>;
public interface IScheduledJobRepository : IRepository<ScheduledJob>;
public interface IRetentionPolicyRepository : IRepository<RetentionPolicy>;
public interface IUserRepository : IRepository<User>
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
}

/// <summary>Repositorio de sesiones con consultas de historial paginadas.</summary>
public interface IRecordingSessionRepository : IRepository<RecordingSession>
{
    Task<IReadOnlyList<RecordingSession>> GetHistoryAsync(
        Guid? channelId, DateTimeOffset from, DateTimeOffset to, int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Cierra las sesiones que quedaron «en grabación» tras un cierre ABRUPTO (crash/kill): las marca como
    /// finalizadas con error y fija <c>EndedAt</c>, para que la BD no arrastre grabaciones colgadas. No toca
    /// los archivos (ya quedaron en disco, fragmentados y reproducibles). Devuelve cuántas cerró.
    /// </summary>
    Task<int> CloseOrphanedAsync(DateTimeOffset endedAt, CancellationToken ct = default);

    /// <summary>
    /// Sesiones del canal cuya grabación TERMINÓ antes de <paramref name="cutoff"/> (con sus segmentos), para
    /// aplicar la retención. Las grabaciones en curso (sin <c>EndedAt</c>) nunca entran.
    /// </summary>
    Task<IReadOnlyList<RecordingSession>> GetEndedBeforeAsync(Guid channelId, DateTimeOffset cutoff, CancellationToken ct = default);
}

/// <summary>Append-only para el registro de eventos/auditoría.</summary>
public interface IEventLogRepository
{
    Task AppendAsync(EventLogEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<EventLogEntry>> QueryAsync(
        Guid? channelId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default);
}

/// <summary>Unidad de trabajo: transacción y SaveChanges para EF Core (SQLite/PostgreSQL).</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
