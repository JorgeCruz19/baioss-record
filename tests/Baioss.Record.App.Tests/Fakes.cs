using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.App.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Sesiones en memoria. Apunta el último rango pedido para comprobar cómo se traducen las fechas.</summary>
internal sealed class FakeSessions : IRecordingSessionRepository
{
    public List<RecordingSession> All { get; } = new();
    public (DateTimeOffset From, DateTimeOffset To)? LastRange { get; private set; }

    public Task<IReadOnlyList<RecordingSession>> GetHistoryAsync(Guid? channelId, DateTimeOffset from, DateTimeOffset to,
        int skip, int take, CancellationToken ct = default)
    {
        LastRange = (from, to);
        IReadOnlyList<RecordingSession> list = All
            .Where(s => s.StartedAt >= from && s.StartedAt <= to && (channelId is null || s.ChannelId == channelId))
            .OrderByDescending(s => s.StartedAt).Skip(skip).Take(take).ToList();
        return Task.FromResult(list);
    }

    public Task<RecordingSession?> GetWithSegmentsAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(All.FirstOrDefault(s => s.Id == id));
    public Task<RecordingSession?> GetAsync(Guid id, CancellationToken ct = default) => GetWithSegmentsAsync(id, ct);
    public Task<IReadOnlyList<RecordingSession>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RecordingSession>>(All.ToList());
    public Task AddAsync(RecordingSession entity, CancellationToken ct = default) { All.Add(entity); return Task.CompletedTask; }
    public Task UpdateAsync(RecordingSession entity, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(Guid id, CancellationToken ct = default) { All.RemoveAll(s => s.Id == id); return Task.CompletedTask; }
    public Task<int> CloseOrphanedAsync(DateTimeOffset endedAt, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<RecordingSession>> GetEndedBeforeAsync(Guid channelId, DateTimeOffset cutoff, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingSession>>(Array.Empty<RecordingSession>());
    public Task<bool> SetProtectionAsync(Guid id, RecordingProtection protection, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlyList<RecordingSession>> GetPurgeCandidatesAsync(int take, string? volumePrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingSession>>(Array.Empty<RecordingSession>());
    public Task<bool> RemoveRecordsAsync(Guid sessionId, IReadOnlyCollection<Guid> segmentIds, CancellationToken ct = default)
        => Task.FromResult(All.RemoveAll(s => s.Id == sessionId) > 0);
}

/// <summary>
/// Servicio de borrado falso: aplica las mismas reglas (<see cref="RecordingDeletionRules"/>) sin tocar el disco —cada
/// segmento cuenta como un archivo presente de <see cref="Segment.SizeBytes"/>— y quita de <see cref="FakeSessions"/> lo
/// que «borra». Apunta qué le pidieron.
/// </summary>
internal sealed class FakeDeletion(FakeSessions sessions) : IRecordingDeletionService
{
    public HashSet<Guid> Live { get; } = new();
    public List<IReadOnlyCollection<Guid>> DeleteCalls { get; } = new();
    public List<RecordingFileIssue> IssuesToReport { get; } = new();

    public IReadOnlySet<Guid> RecordingNow() => Live;

    public RecordingDeletionPlan Plan(IReadOnlyCollection<RecordingSession> list)
    {
        var deletable = list.Where(s => !RecordingDeletionRules.IsInProgress(s, Live) && !RecordingDeletionRules.IsProtected(s)).ToList();
        return new RecordingDeletionPlan(deletable.Count,
            deletable.Sum(s => s.Segments.Count), deletable.Sum(s => s.TotalBytes), 0, Array.Empty<string>(),
            list.Count(s => RecordingDeletionRules.IsInProgress(s, Live)),
            list.Count(s => !RecordingDeletionRules.IsInProgress(s, Live) && RecordingDeletionRules.IsProtected(s)));
    }

    public Task<RecordingDeletionResult> DeleteAsync(IReadOnlyCollection<Guid> sessionIds, string? operatorName,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        DeleteCalls.Add(sessionIds.ToList());
        var blocked = IssuesToReport.Count;
        var doomed = sessions.All.Where(s => sessionIds.Contains(s.Id)).Skip(blocked).ToList();
        foreach (var s in doomed) sessions.All.Remove(s);
        int n = 0;
        foreach (var _ in sessionIds) progress?.Report(++n);
        return Task.FromResult(new RecordingDeletionResult(doomed.Count, doomed.Sum(s => s.Segments.Count),
            doomed.Sum(s => s.TotalBytes), 0, 0, 0, IssuesToReport.ToList(), 0));
    }
}

/// <summary>Registro de actividad en memoria.</summary>
internal sealed class FakeEvents : IEventLogRepository
{
    public List<EventLogEntry> All { get; } = new();
    public List<long> Deleted { get; } = new();

    public Task AppendAsync(EventLogEntry entry, CancellationToken ct = default) { All.Add(entry); return Task.CompletedTask; }

    public Task<IReadOnlyList<EventLogEntry>> QueryAsync(Guid? channelId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EventLogEntry>>(All
            .Where(e => e.Timestamp >= from && e.Timestamp <= to && (channelId is null || e.ChannelId == channelId))
            .OrderByDescending(e => e.Timestamp).Take(take).ToList());

    public Task<int> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        Deleted.AddRange(ids);
        return Task.FromResult(All.RemoveAll(e => ids.Contains(e.Id)));
    }
}

/// <summary>Bus que solo apunta lo publicado.</summary>
internal sealed class FakeBus : IEventBus
{
    public List<IDomainEvent> Published { get; } = new();
    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct = default) { Published.Add(domainEvent); return Task.CompletedTask; }
    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent => new Nothing();
    private sealed class Nothing : IDisposable { public void Dispose() { } }
}

/// <summary>La pregunta de confirmación: apunta qué se preguntó y contesta lo que diga la prueba.</summary>
internal sealed class FakeConfirm(bool answer)
{
    public List<(string Text, string Title)> Asked { get; } = new();
    public bool Answer { get; set; } = answer;
    public bool Ask(string text, string title) { Asked.Add((text, title)); return Answer; }
}
