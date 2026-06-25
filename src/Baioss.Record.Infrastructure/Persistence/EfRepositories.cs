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
            .Where(s => s.ChannelId == channelId && s.EndedAt != null && s.EndedAt < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Repositorio append-only del registro de eventos/auditoría (clave entera autoincremental).</summary>
public sealed class EventLogRepository(IDbContextFactory<BaiossDbContext> factory) : IEventLogRepository
{
    public async Task AppendAsync(EventLogEntry entry, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.EventLog.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EventLogEntry>> QueryAsync(
        Guid? channelId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default)
    {
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
