using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
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

    // --- Conversores de value objects → columnas primitivas (SQLite/PostgreSQL) ---
    // Los value objects del dominio son structs inmutables; EF los persiste como texto/entero
    // mediante estos conversores, sin tablas owned ni columnas desnormalizadas.
    private static readonly ValueConverter<Resolution, string> ResolutionConverter = new(
        v => $"{v.Width}x{v.Height}",
        v => ParseResolution(v));

    private static readonly ValueConverter<FrameRate, string> FrameRateConverter = new(
        v => $"{v.Numerator}/{v.Denominator}",
        v => ParseFrameRate(v));

    private static readonly ValueConverter<Bitrate, long> BitrateConverter = new(
        v => v.BitsPerSecond,
        v => new Bitrate(v));

    // MaxBitrate es opcional (Bitrate?): null = sin límite explícito. Necesita su propio
    // conversor nullable porque Bitrate es un struct y EF no lo mapea por convención.
    private static readonly ValueConverter<Bitrate?, long?> NullableBitrateConverter = new(
        v => v.HasValue ? v.Value.BitsPerSecond : null,
        v => v.HasValue ? new Bitrate(v.Value) : null);

    private static readonly ValueConverter<Timecode, string> TimecodeConverter = new(
        v => v.ToString(),
        v => Timecode.Parse(v));

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite no traduce comparaciones/orden sobre DateTimeOffset almacenado como texto.
        // El conversor integrado lo codifica en un long ordenable preservando el offset, de modo
        // que los filtros de rango y ORDER BY de historial/auditoría se ejecutan en el servidor.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RecordingSession>(e =>
        {
            e.HasMany(s => s.Segments).WithOne().HasForeignKey(s => s.SessionId);
            // Índices para historial (por canal, ordenado por fecha) y retención (por canal + fin). La tabla
            // Sessions crece sin parar en 24/7; sin índice eran table scans + sort en memoria crecientes que
            // competían con las escrituras de segmentos por el único escritor de SQLite. (Auditoría #22.)
            e.HasIndex(s => new { s.ChannelId, s.StartedAt });
            e.HasIndex(s => new { s.ChannelId, s.EndedAt });
            e.HasIndex(s => s.StartedAt);
            e.Property(s => s.Metadata).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
            e.Property(s => s.Resolution).HasConversion(ResolutionConverter);
            e.Property(s => s.FrameRate).HasConversion(FrameRateConverter);
            e.Property(s => s.StartTimecode).HasConversion(TimecodeConverter);
            e.Property(s => s.EndTimecode).HasConversion(TimecodeConverter);
        });

        b.Entity<Segment>(e =>
        {
            e.HasIndex(s => new { s.SessionId, s.Index }).IsUnique();
            e.Property(s => s.StartTimecode).HasConversion(TimecodeConverter);
            e.Property(s => s.EndTimecode).HasConversion(TimecodeConverter);
        });

        b.Entity<InputSource>(e =>
        {
            e.Property(s => s.ExpectedResolution).HasConversion(ResolutionConverter);
            e.Property(s => s.ExpectedFrameRate).HasConversion(FrameRateConverter);
            // Diccionario de parámetros por protocolo: se persiste como JSON.
            e.Property(s => s.Parameters).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
        });

        b.Entity<RecordingProfile>(e =>
        {
            e.Property(p => p.VideoBitrate).HasConversion(BitrateConverter);
            e.Property(p => p.AudioBitrate).HasConversion(BitrateConverter);
            e.Property(p => p.MaxBitrate).HasConversion(NullableBitrateConverter);
            e.Property(p => p.TargetResolution).HasConversion(ResolutionConverter);
            e.Property(p => p.OutputFrameRate).HasConversion(FrameRateConverter);
            // Sub-políticas y destinos de streaming se persisten en Fase 2 (owned/JSON dedicado).
            // SlateOnSignalLoss va junto a la segmentación: ambos son ajustes operativos por sesión
            // (ignorados aquí para no cambiar el esquema; se persistirán con las sub-políticas en Fase 2).
            e.Ignore(p => p.StreamTargets);
            e.Ignore(p => p.Segmentation);
            e.Ignore(p => p.Proxy);
            e.Ignore(p => p.SlateOnSignalLoss);
        });

        b.Entity<EventLogEntry>().HasIndex(x => x.Timestamp);
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();

        base.OnModelCreating(b);
    }

    private static Resolution ParseResolution(string value)
    {
        var parts = value.Split('x', 2);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            ? new Resolution(w, h)
            : default;
    }

    private static FrameRate ParseFrameRate(string value)
    {
        var parts = value.Split('/', 2);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? new FrameRate(n, d)
            : default;
    }

    Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken ct) => SaveChangesAsync(ct);
}
