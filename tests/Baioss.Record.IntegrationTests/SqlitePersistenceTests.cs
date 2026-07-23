using Microsoft.Extensions.DependencyInjection;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;
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
    public async Task Channel_OutputDirectory_RoundTrips_AndDefaultsNull()
    {
        var channels = _sp.GetRequiredService<IChannelRepository>();

        // Canal recién creado SIN carpeta configurada: OutputDirectory = null ⇒ el host usará el default.
        var channel = new Channel { Key = "A", Name = "Canal A" };
        await channels.AddAsync(channel);
        var fresh = await channels.GetAsync(channel.Id);
        Assert.NotNull(fresh);
        Assert.Null(fresh!.OutputDirectory);

        // El operador elige una carpeta: se persiste y SOBREVIVE (se lee de vuelta idéntica). Esto es lo que
        // antes NO ocurría: la ruta solo vivía en el motor en memoria y volvía al default al reiniciar.
        fresh.OutputDirectory = @"D:\capturer01";
        await channels.UpdateAsync(fresh);
        var afterSet = await channels.GetAsync(channel.Id);
        Assert.Equal(@"D:\capturer01", afterSet!.OutputDirectory);
    }

    [Fact]
    public async Task GetPurgeCandidates_FiltersByVolumePrefix_ForMultiDiskCleanup()
    {
        var sources = _sp.GetRequiredService<IInputSourceRepository>();
        var profiles = _sp.GetRequiredService<IRecordingProfileRepository>();
        var channels = _sp.GetRequiredService<IChannelRepository>();
        var sessions = _sp.GetRequiredService<IRecordingSessionRepository>();
        var segments = _sp.GetRequiredService<IRepository<Segment>>();

        var source = new InputSource { Name = "S", Type = InputType.File, Uri = @"C:\x.mp4", ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25 };
        var profile = new RecordingProfile { Name = "P", VideoCodec = VideoCodec.H264x264, VideoBitrate = Bitrate.FromMbps(8), AudioBitrate = Bitrate.FromKbps(256), Container = ContainerFormat.Mp4 };
        var channel = new Channel { Key = "A", Name = "Canal A", InputSourceId = source.Id, ProfileId = profile.Id };
        await sources.AddAsync(source);
        await profiles.AddAsync(profile);
        await channels.AddAsync(channel);

        // Dos grabaciones FINALIZADAS (candidatas a purga), una con archivo en el disco D: y otra en C:.
        async Task AddEnded(string filePath, DateTimeOffset ended)
        {
            var s = new RecordingSession
            {
                ChannelId = channel.Id, ProfileId = profile.Id, InputSourceId = source.Id,
                StartedAt = ended.AddMinutes(-10), EndedAt = ended, State = RecordingState.Idle,
                Resolution = Resolution.Hd1080, FrameRate = FrameRate.P25,
                VideoCodec = VideoCodec.H264x264, AudioCodec = AudioCodec.Aac,
            };
            await sessions.AddAsync(s);
            await segments.AddAsync(new Segment { SessionId = s.Id, Index = 0, FilePath = filePath, Status = SegmentStatus.Completed, SizeBytes = 1000, StartedAt = s.StartedAt, EndedAt = ended });
        }
        await AddEnded(@"D:\capturer01\A_1.mp4", DateTimeOffset.UtcNow.AddDays(-2));
        await AddEnded(@"C:\rec\A_2.mp4", DateTimeOffset.UtcNow.AddDays(-1));

        // Sin prefijo → ambas (comportamiento clásico de un solo disco).
        var all = await sessions.GetPurgeCandidatesAsync(10, null);
        Assert.Equal(2, all.Count);

        // Prefijo «D:\» → SOLO la de D: (multi-disco: liberar D: no debe borrar material de C:).
        var onlyD = await sessions.GetPurgeCandidatesAsync(10, @"D:\");
        Assert.Single(onlyD);
        Assert.All(onlyD, s => Assert.Contains(s.Segments, seg => seg.FilePath!.StartsWith(@"D:\")));
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
            MaxBitrate = Bitrate.FromMbps(12), PixelFormat = PixelFormat.Yuv422p10le, AudioOnly = false,
        };
        // Perfil con MaxBitrate nulo: cubre la rama null del conversor nullable (el caso por defecto
        // de la app). Un MaxBitrate sin conversor rompía el modelo EF y tiraba la app a modo simulado.
        var audioProfile = new RecordingProfile
        {
            Name = "WAV", VideoCodec = VideoCodec.H264x264, AudioOnly = true,
            VideoBitrate = Bitrate.FromMbps(8), AudioBitrate = Bitrate.FromKbps(256),
            Container = ContainerFormat.Wav, MaxBitrate = null,
        };
        var channel = new Channel { Key = "A", Name = "Canal A", InputSourceId = source.Id, ProfileId = profile.Id };
        await sources.AddAsync(source);
        await profiles.AddAsync(profile);
        await profiles.AddAsync(audioProfile);
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
        Assert.Equal(12_000_000, profileBack.MaxBitrate!.Value.BitsPerSecond);
        Assert.Equal(PixelFormat.Yuv422p10le, profileBack.PixelFormat);
        Assert.False(profileBack.AudioOnly);

        var audioBack = await profiles.GetAsync(audioProfile.Id);
        Assert.NotNull(audioBack);
        Assert.Null(audioBack!.MaxBitrate);   // la rama null del conversor sobrevive el round-trip
        Assert.True(audioBack.AudioOnly);

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

    [Fact]
    public async Task CloseOrphaned_ClosesActiveSessions_LeavesFinishedUntouched()
    {
        var sources = _sp.GetRequiredService<IInputSourceRepository>();
        var profiles = _sp.GetRequiredService<IRecordingProfileRepository>();
        var channels = _sp.GetRequiredService<IChannelRepository>();
        var sessions = _sp.GetRequiredService<IRecordingSessionRepository>();

        var source = new InputSource { Name = "Clip", Type = InputType.File, Uri = @"C:\x\c.mp4" };
        var profile = new RecordingProfile
        {
            Name = "MP4", VideoCodec = VideoCodec.H264x264,
            VideoBitrate = Bitrate.FromMbps(8), AudioBitrate = Bitrate.FromKbps(256), Container = ContainerFormat.Mp4,
        };
        var channel = new Channel { Key = "A", Name = "Canal A", InputSourceId = source.Id, ProfileId = profile.Id };
        await sources.AddAsync(source);
        await profiles.AddAsync(profile);
        await channels.AddAsync(channel);

        // Huérfana: quedó «grabando» sin EndedAt (simula un crash a mitad de grabación).
        var orphan = new RecordingSession
        {
            ChannelId = channel.Id, ProfileId = profile.Id, InputSourceId = source.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30), State = RecordingState.Recording,
        };
        // Terminada correctamente: Idle + EndedAt → NO debe tocarse.
        var done = new RecordingSession
        {
            ChannelId = channel.Id, ProfileId = profile.Id, InputSourceId = source.Id,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1), EndedAt = DateTimeOffset.UtcNow.AddMinutes(-50),
            State = RecordingState.Idle,
        };
        await sessions.AddAsync(orphan);
        await sessions.AddAsync(done);

        int closed = await sessions.CloseOrphanedAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, closed); // solo la huérfana
        var orphanBack = await sessions.GetAsync(orphan.Id);
        Assert.Equal(RecordingState.Error, orphanBack!.State);
        Assert.NotNull(orphanBack.EndedAt);
        var doneBack = await sessions.GetAsync(done.Id);
        Assert.Equal(RecordingState.Idle, doneBack!.State); // la terminada queda intacta
    }

    [Fact]
    public async Task ApplyRetention_DeletesOldRecordings_KeepsRecent()
    {
        var storage = _sp.GetRequiredService<IStorageManager>();
        var sources = _sp.GetRequiredService<IInputSourceRepository>();
        var profiles = _sp.GetRequiredService<IRecordingProfileRepository>();
        var channels = _sp.GetRequiredService<IChannelRepository>();
        var sessions = _sp.GetRequiredService<IRecordingSessionRepository>();
        var segments = _sp.GetRequiredService<IRepository<Segment>>();

        var source = new InputSource { Name = "C", Type = InputType.File, Uri = @"C:\x\c.mp4" };
        var profile = new RecordingProfile
        {
            Name = "MP4", VideoCodec = VideoCodec.H264x264,
            VideoBitrate = Bitrate.FromMbps(8), AudioBitrate = Bitrate.FromKbps(256), Container = ContainerFormat.Mp4,
        };
        var channel = new Channel { Key = "A", Name = "Canal A", InputSourceId = source.Id, ProfileId = profile.Id };
        await sources.AddAsync(source);
        await profiles.AddAsync(profile);
        await channels.AddAsync(channel);

        var dir = Path.Combine(Path.GetTempPath(), $"baioss-ret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var oldFile = Path.Combine(dir, "old.mp4"); await File.WriteAllTextAsync(oldFile, "x");
            var newFile = Path.Combine(dir, "new.mp4"); await File.WriteAllTextAsync(newFile, "x");
            var now = DateTimeOffset.UtcNow;

            // Grabación terminada hace 40 días → expira con RetentionDays=30.
            var oldSession = new RecordingSession
            {
                ChannelId = channel.Id, ProfileId = profile.Id, InputSourceId = source.Id,
                StartedAt = now.AddDays(-41), EndedAt = now.AddDays(-40), State = RecordingState.Idle,
            };
            await sessions.AddAsync(oldSession);
            await segments.AddAsync(new Segment
            {
                SessionId = oldSession.Id, Index = 0, FilePath = oldFile,
                Status = SegmentStatus.Completed, StartedAt = oldSession.StartedAt, EndedAt = oldSession.EndedAt!.Value,
            });

            // Grabación de ayer → se conserva.
            var newSession = new RecordingSession
            {
                ChannelId = channel.Id, ProfileId = profile.Id, InputSourceId = source.Id,
                StartedAt = now.AddDays(-2), EndedAt = now.AddDays(-1), State = RecordingState.Idle,
            };
            await sessions.AddAsync(newSession);
            await segments.AddAsync(new Segment
            {
                SessionId = newSession.Id, Index = 0, FilePath = newFile,
                Status = SegmentStatus.Completed, StartedAt = newSession.StartedAt, EndedAt = newSession.EndedAt!.Value,
            });

            await storage.ApplyRetentionAsync(new RetentionPolicy
            {
                ChannelId = channel.Id, RetentionDays = 30, Action = RetentionAction.Delete,
            });

            Assert.False(File.Exists(oldFile), "El archivo de >30 días debió borrarse.");
            Assert.True(File.Exists(newFile), "El archivo reciente debe conservarse.");
            Assert.Null(await sessions.GetAsync(oldSession.Id));    // la sesión vieja se retiró de la BD
            Assert.NotNull(await sessions.GetAsync(newSession.Id)); // la reciente queda intacta
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
