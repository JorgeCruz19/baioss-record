using Baioss.Record.Domain;
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
    /// aplicar la retención. Las grabaciones en curso (sin <c>EndedAt</c>) y las PROTEGIDAS nunca entran.
    /// </summary>
    Task<IReadOnlyList<RecordingSession>> GetEndedBeforeAsync(Guid channelId, DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Marca (o quita) la protección de una sesión frente a la retención automática. Devuelve false si no existe.</summary>
    Task<bool> SetProtectionAsync(Guid id, RecordingProtection protection, CancellationToken ct = default);

    /// <summary>Sesiones FINALIZADAS y NO protegidas, más ANTIGUAS primero (por fin), para liberar espacio
    /// (retención por espacio). <paramref name="take"/> limita el lote. <paramref name="volumePrefix"/> (p. ej.
    /// «D:\») ACOTA a las grabaciones cuyo archivo está EN ESE VOLUMEN — imprescindible con varios discos, para
    /// no borrar de un disco distinto al que se quiere liberar; <c>null</c>/vacío = cualquier disco. (Fase 2 / multi-disco.)</summary>
    Task<IReadOnlyList<RecordingSession>> GetPurgeCandidatesAsync(int take, string? volumePrefix, CancellationToken ct = default);

    /// <summary>Una sesión CON sus segmentos (<see cref="IRepository{T}.GetAsync"/> no los trae), o null si no existe.</summary>
    Task<RecordingSession?> GetWithSegmentsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Borra de la BD los segmentos <paramref name="segmentIds"/> de la sesión y, si con eso se queda sin ninguno, también
    /// la sesión, en UNA transacción: nunca queda un segmento sin su sesión. Los archivos no se tocan (los borra quien
    /// llama, antes). Devuelve true si la sesión se borró.
    /// </summary>
    Task<bool> RemoveRecordsAsync(Guid sessionId, IReadOnlyCollection<Guid> segmentIds, CancellationToken ct = default);
}

/// <summary>Registro de eventos/auditoría: lo escribe el sistema; solo se borra por antigüedad (poda) o cuando el
/// operador elige entradas concretas en la ventana «Actividad» (que deja constancia con <c>AuditEntriesDeleted</c>).</summary>
public interface IEventLogRepository
{
    Task AppendAsync(EventLogEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<EventLogEntry>> QueryAsync(
        Guid? channelId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default);

    /// <summary>Borra las entradas <paramref name="ids"/>. Devuelve cuántas borró (las que ya no existían no cuentan).</summary>
    Task<int> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);
}

/// <summary>Unidad de trabajo: transacción y SaveChanges para EF Core (SQLite/PostgreSQL).</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
