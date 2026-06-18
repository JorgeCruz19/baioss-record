using Microsoft.Extensions.DependencyInjection;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Infrastructure;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Persistencia real sobre EF Core SQLite: esquema creado con los conversores de value
/// objects y round-trip de las entidades de Fase 1, incluida la consulta de historial por
/// rango de fechas (que exige el conversor de <see cref="DateTimeOffset"/>).
/// </summary>
public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"baioss-it-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public SqlitePersistenceTests()
    {
        _sp = new ServiceCollection()
            .AddLogging()
            .AddBaiossInfrastructure(_dbPath)
            .BuildServiceProvider();
        _sp.EnsureBaiossDatabaseCreated();
    }

    [Fact]
    public async Task RoundTrip_PreservesValueObjectsAndHistory()
    {
        var sources = _sp.GetRequiredService<IInputSourceRepository>();
        var profiles = _sp.GetRequiredService<IRecordingProfileRepository>();
        var channels = _sp.GetRequiredService<IChannelRepository>();
        var sessions = _sp.GetRequiredService<IRecordingSessionRepository>();
        var segments = _sp.GetRequiredService<IRepository<Segment>>();

        var source = new InputSource
        {
            Name = "Clip A", Type = InputType.File, Uri = @"C:\x\clip.mp4",
            Parameters = { ["loop"] = "1", ["realtime"] = "1" },
            ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P2997,
        };
        var profile = new RecordingProfile
        {
            Name = "MP4", VideoCodec = VideoCodec.H264x264,
            VideoBitrate = Bitrate.FromMbps(8), AudioBitrate = Bitrate.FromKbps(256), Container = ContainerFormat.Mp4,
        };
        var channel = new Channel { Key = "A", Name = "Canal A", InputSourceId = source.Id, ProfileId = profile.Id };
        await sources.AddAsync(source);
        await profiles.AddAsync(profile);
        await channels.AddAsync(channel);

        var session = new RecordingSession
        {
            ChannelId = channel.Id, ProfileId = profile.Id, InputSourceId = source.Id,
            StartedAt = DateTimeOffset.UtcNow, State = RecordingState.Recording,
            Resolution = Resolution.Hd1080, FrameRate = FrameRate.P25,
            StartTimecode = new Timecode(1, 2, 3, 4),
            VideoCodec = VideoCodec.H264x264, AudioCodec = AudioCodec.Aac,
        };
        await sessions.AddAsync(session);
        await segments.AddAsync(new Segment
        {
            SessionId = session.Id, Index = 0, FilePath = @"C:\rec\a_0.mp4",
            Status = SegmentStatus.Completed, SizeBytes = 12_345,
            StartedAt = session.StartedAt, EndedAt = session.StartedAt.AddMinutes(15),
            EndTimecode = new Timecode(0, 15, 0, 0),
        });

        session.EndedAt = DateTimeOffset.UtcNow;
        session.State = RecordingState.Idle;
        await sessions.UpdateAsync(session);

        // --- Lectura de vuelta ---
        var sourceBack = await sources.GetAsync(source.Id);
        Assert.NotNull(sourceBack);
        Assert.Equal(Resolution.Hd720, sourceBack!.ExpectedResolution);
        Assert.Equal(FrameRate.P2997, sourceBack.ExpectedFrameRate);
        Assert.Equal("1", sourceBack.Parameters.GetValueOrDefault("loop"));

        var profileBack = await profiles.GetAsync(profile.Id);
        Assert.NotNull(profileBack);
        Assert.Equal(8_000_000, profileBack!.VideoBitrate.BitsPerSecond);
        Assert.Equal(256_000, profileBack.AudioBitrate.BitsPerSecond);

        // Historial por rango de fechas (DateTimeOffset traducido en el servidor).
        var history = await sessions.GetHistoryAsync(
            channel.Id, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), 0, 10);
        var sessionBack = Assert.Single(history);
        Assert.Equal(RecordingState.Idle, sessionBack.State);
        Assert.Equal(Resolution.Hd1080, sessionBack.Resolution);
        Assert.Equal(new Timecode(1, 2, 3, 4), sessionBack.StartTimecode);
        var segmentBack = Assert.Single(sessionBack.Segments);
        Assert.Equal(12_345, segmentBack.SizeBytes);
        Assert.Equal(new Timecode(0, 15, 0, 0), segmentBack.EndTimecode);
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
