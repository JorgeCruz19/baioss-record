using Microsoft.EntityFrameworkCore;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Persistence;

namespace Baioss.Record.Infrastructure.Persistence;

/// <summary>
/// DbContext de EF Core. El mismo modelo corre sobre SQLite (config local, single-box)
/// o PostgreSQL (entornos empresariales / multi-nodo) cambiando solo el provider en DI.
/// </summary>
public sealed class BaiossDbContext(DbContextOptions<BaiossDbContext> options)
    : DbContext(options), IUnitOfWork
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<InputSource> InputSources => Set<InputSource>();
    public DbSet<RecordingProfile> Profiles => Set<RecordingProfile>();
    public DbSet<RecordingSession> Sessions => Set<RecordingSession>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<EventLogEntry> EventLog => Set<EventLogEntry>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RecordingSession>(e =>
        {
            e.HasMany(s => s.Segments).WithOne().HasForeignKey(s => s.SessionId);
            e.Property(s => s.Metadata).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
        });

        b.Entity<Segment>().HasIndex(s => new { s.SessionId, s.Index }).IsUnique();
        b.Entity<EventLogEntry>().HasIndex(x => x.Timestamp);
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();

        // Value objects y diccionarios complejos se mapean como JSON/owned types
        // (omitido en el scaffold por brevedad — ver migraciones EF).
        b.Entity<InputSource>().Ignore(s => s.Parameters);
        b.Entity<RecordingProfile>().Ignore(p => p.StreamTargets);

        base.OnModelCreating(b);
    }

    Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken ct) => SaveChangesAsync(ct);
}
