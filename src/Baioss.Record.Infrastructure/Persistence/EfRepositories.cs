using Microsoft.EntityFrameworkCore;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Persistence;

namespace Baioss.Record.Infrastructure.Persistence;

/// <summary>
/// Repositorio genérico EF Core para entidades con clave <c>Guid Id</c>. Crea un
/// <see cref="BaiossDbContext"/> de corta vida por operación mediante el factory —
/// patrón seguro para servicios de larga vida (canales 24/7) y escrituras concurrentes
/// desde múltiples canales. Cada operación de escritura confirma su propia transacción.
/// </summary>
public class EfRepository<T> : IRepository<T> where T : class
{
    protected readonly IDbContextFactory<BaiossDbContext> Factory;

    public EfRepository(IDbContextFactory<BaiossDbContext> factory) => Factory = factory;

    public async Task<T?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<T>().FindAsync(new object?[] { id }, ct).AsTask().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<T>().AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAsync(T entity, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Update(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<T>().FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (entity is null) return;
        db.Remove(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

public sealed class ChannelRepository(IDbContextFactory<BaiossDbContext> f)
    : EfRepository<Channel>(f), IChannelRepository;

public sealed class InputSourceRepository(IDbContextFactory<BaiossDbContext> f)
    : EfRepository<InputSource>(f), IInputSourceRepository;

public sealed class RecordingProfileRepository(IDbContextFactory<BaiossDbContext> f)
    : EfRepository<RecordingProfile>(f), IRecordingProfileRepository;

public sealed class ScheduledJobRepository(IDbContextFactory<BaiossDbContext> f)
    : EfRepository<ScheduledJob>(f), IScheduledJobRepository;

public sealed class RetentionPolicyRepository(IDbContextFactory<BaiossDbContext> f)
    : EfRepository<RetentionPolicy>(f), IRetentionPolicyRepository;

public sealed class UserRepository(IDbContextFactory<BaiossDbContext> f)
    : EfRepository<User>(f), IUserRepository
{
    public async Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username, ct).ConfigureAwait(false);
    }
}

public sealed class RecordingSessionRepository(IDbContextFactory<BaiossDbContext> f)
    : EfRepository<RecordingSession>(f), IRecordingSessionRepository
{
    public async Task<IReadOnlyList<RecordingSession>> GetHistoryAsync(
        Guid? channelId, DateTimeOffset from, DateTimeOffset to, int skip, int take, CancellationToken ct = default)
    {
        // A UTC, como se guardan: la columna es el número del DateTimeOffsetToBinaryConverter (hora del reloj LOCAL de la fecha
        // + su desfase), que solo se ordena bien entre fechas con el mismo desfase. Un límite en hora local (−06:00) dejaba
        // fuera las grabaciones de las últimas seis horas del día.
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Sessions.AsNoTracking()
            .Include(s => s.Segments)
            .Where(s => s.StartedAt >= from && s.StartedAt <= to);
        if (channelId is { } id) query = query.Where(s => s.ChannelId == id);
        return await query
            .OrderByDescending(s => s.StartedAt)
            .Skip(skip).Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CloseOrphanedAsync(DateTimeOffset endedAt, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Una sesión «abierta» (sin EndedAt) en cualquier estado de actividad quedó colgada por un cierre
        // abrupto: la sesión de una grabación que terminó bien ya tiene EndedAt e Idle.
        var orphaned = await db.Sessions
            .Where(s => s.EndedAt == null && s.State != RecordingState.Idle && s.State != RecordingState.Error)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var s in orphaned)
        {
            s.State = RecordingState.Error;
            s.EndedAt = endedAt;
        }
        if (orphaned.Count > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return orphaned.Count;
    }

    public async Task<IReadOnlyList<RecordingSession>> GetEndedBeforeAsync(Guid channelId, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Sessions.AsNoTracking()
            .Include(s => s.Segments)
            // Protección enforced en la propia consulta (defensa en profundidad): una sesión protegida NUNCA
            // entra en la retención, aunque el llamador olvide comprobarlo. (Gestión de almacenamiento — Fase 1.)
            .Where(s => s.ChannelId == channelId && s.EndedAt != null && s.EndedAt < cutoff
                        && s.Protection == RecordingProtection.None)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> SetProtectionAsync(Guid id, RecordingProtection protection, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false); // tracked → se guarda
        if (s is null) return false;
        s.Protection = protection;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<RecordingSession>> GetPurgeCandidatesAsync(int take, string? volumePrefix, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Sessions.AsNoTracking()
            .Include(s => s.Segments)
            .Where(s => s.EndedAt != null && s.Protection == RecordingProtection.None); // finalizadas y NO protegidas
        // Acota al VOLUMEN pedido (multi-disco): solo sesiones con algún segmento cuyo archivo está en ese disco.
        // StartsWith → LIKE 'prefijo%' (SQLite es insensible a mayúsculas en ASCII, así que «D:\» casa con «d:\»).
        if (!string.IsNullOrEmpty(volumePrefix))
            q = q.Where(s => s.Segments.Any(seg => seg.FilePath != null && seg.FilePath.StartsWith(volumePrefix)));
        return await q
            .OrderBy(s => s.EndedAt)                                                   // más antiguas primero
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<RecordingSession?> GetWithSegmentsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Sessions.AsNoTracking()
            .Include(s => s.Segments)
            .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveRecordsAsync(Guid sessionId, IReadOnlyCollection<Guid> segmentIds, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Se cargan TODOS los segmentos de la sesión (unos pocos, o cientos en una segmentada de días) y se eligen en
        // memoria: así se sabe si queda alguno sin una segunda consulta, y SaveChanges borra segmentos y sesión en una sola
        // transacción.
        var wanted = segmentIds as IReadOnlySet<Guid> ?? segmentIds.ToHashSet();
        var all = await db.Segments.Where(s => s.SessionId == sessionId).ToListAsync(ct).ConfigureAwait(false);
        var doomed = all.Where(s => wanted.Contains(s.Id)).ToList();
        db.Segments.RemoveRange(doomed);

        bool sessionRemoved = false;
        if (doomed.Count == all.Count)
        {
            var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct).ConfigureAwait(false);
            if (session is not null) { db.Sessions.Remove(session); sessionRemoved = true; }
        }
        if (doomed.Count > 0 || sessionRemoved) await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return sessionRemoved;
    }
}

/// <summary>Repositorio del registro de eventos/auditoría (clave entera autoincremental). Lo escribe el sistema; se borra
/// por antigüedad (poda del <c>EventLogWriter</c>) o por entradas concretas que elige el operador.</summary>
public sealed class EventLogRepository(IDbContextFactory<BaiossDbContext> factory) : IEventLogRepository
{
    /// <summary>Entradas por DELETE: la ventana borra hasta miles de una vez y SQLite tiene un único escritor (las
    /// grabaciones escriben sus segmentos mientras tanto); por lotes, ninguno lo retiene más de un momento.</summary>
    internal const int DeleteBatchSize = 500;

    public async Task<int> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        int removed = 0;
        foreach (var batch in ids.Distinct().Chunk(DeleteBatchSize))
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            removed += await db.EventLog.Where(e => batch.Contains(e.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        return removed;
    }

    public async Task AppendAsync(EventLogEntry entry, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.EventLog.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EventLogEntry>> QueryAsync(
        Guid? channelId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default)
    {
        from = from.ToUniversalTime(); // como en el historial de grabaciones: la columna solo se compara bien en UTC
        to = to.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.EventLog.AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to);
        if (channelId is { } id) query = query.Where(e => e.ChannelId == id);
        return await query
            .OrderByDescending(e => e.Timestamp)
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
