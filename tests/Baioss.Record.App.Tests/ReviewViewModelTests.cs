using Baioss.Record.Application.Clips;
using Baioss.Record.Application.Localization;
using Baioss.Record.App.Recordings;
using Baioss.Record.App.Review;
using Baioss.Record.App.Storage;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Xunit;

namespace Baioss.Record.App.Tests;

/// <summary>Servicio de clips falso: una línea de tiempo de 30 s a 25 fps (tres segmentos de 10 s) y apunta lo que se le pide.</summary>
internal sealed class FakeClips : IClipExportService
{
    public List<ClipRequest> Requests { get; } = new();
    public ClipException? LoadFails { get; init; }
    public ClipException? ExportFails { get; set; }
    public string ClipsFolder => @"C:\clips";
    public string DefaultClipsFolder => @"C:\Baioss\Record\clips";

    public static RecordingTimeline Timeline(Guid session) => new(session, Guid.NewGuid(), FrameRate.P25,
        Enumerable.Range(0, 3).Select(i => new TimelinePiece
        {
            SegmentId = Guid.NewGuid(), Index = i, FilePath = $@"D:\rec\Noticias_{i + 1}.mp4", Frames = 250,
            StartedAt = new DateTimeOffset(2026, 10, 21, 20, 0, 10 * i, TimeSpan.Zero),
            Media = new MediaDescription
            {
                Video = new MediaStreamInfo { Type = "video", Codec = "h264", Width = 1920, Height = 1080, FrameRate = FrameRate.P25, Start = 0, Frames = 250 },
                Audio = new[] { new MediaStreamInfo { Type = "audio", Codec = "aac", SampleRate = 48_000, Channels = 2, Start = 0 } },
            },
        }).ToList());

    public Task<RecordingTimeline> LoadTimelineAsync(Guid sessionId, CancellationToken ct = default)
        => LoadFails is { } e ? Task.FromException<RecordingTimeline>(e) : Task.FromResult(Timeline(sessionId));

    public Task<ClipResult> ExportAsync(ClipRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (ExportFails is { } e) return Task.FromException<ClipResult>(e);
        var t = Timeline(request.SessionId);
        return Task.FromResult(new ClipResult(@"C:\clips\Noticias (00.00.04-00.00.06).mp4", request.InFrame, request.OutFrame,
            t.TimeOf(request.InFrame), t.TimeOf(request.OutFrame), request.Mode, 2_500_000)
        { PreRoll = request.Mode == ClipMode.Fast ? TimeSpan.FromSeconds(0.36) : TimeSpan.Zero });
    }
}

/// <summary>
/// Ventana «Revisar»: las marcas (la salida es el ÚLTIMO cuadro que entra, como en los editores), la duración del clip, lo que se
/// le pide al servicio al exportar y lo que se le dice al operador si algo falla. Sin reproductor (no hay FFmpeg en estas pruebas).
/// </summary>
[Collection("Localizer")]
public sealed class ReviewViewModelTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;
    public ReviewViewModelTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    private static RecordingSession Session()
    {
        var s = new RecordingSession
        {
            ChannelId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), InputSourceId = Guid.NewGuid(),
            StartedAt = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero), EndedAt = new DateTimeOffset(2026, 10, 21, 20, 0, 30, TimeSpan.Zero),
        };
        for (int i = 0; i < 3; i++) s.Segments.Add(new Segment { SessionId = s.Id, Index = i, FilePath = $@"D:\rec\Noticias_{i + 1}.mp4" });
        return s;
    }

    private static async Task<ReviewViewModel> OpenAsync(FakeClips clips, RecordingSession? session = null)
    {
        var vm = new ReviewViewModel(clips, session ?? Session(), "A", _ => null);
        await vm.Loading;
        return vm;
    }

    [Fact]
    public async Task Marks_InAndOut_AreBothPartOfTheClip()
    {
        var clips = new FakeClips();
        var vm = await OpenAsync(clips);
        Assert.True(vm.IsLoaded);
        Assert.Equal(749, vm.Maximum);
        Assert.Equal("00:00:30:00", vm.TotalText);
        Assert.False(vm.ExportCommand.CanExecute(null)); // sin marcas no hay clip

        vm.Position = 100;
        Assert.Equal("00:00:04:00", vm.PositionText);
        // Hora del día (local) de la pieza + la posición.
        var wall = new DateTimeOffset(2026, 10, 21, 20, 0, 4, TimeSpan.Zero).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(wall, vm.WallClockText);
        vm.MarkInCommand.Execute(null);
        vm.Position = 150;
        vm.MarkOutCommand.Execute(null);
        Assert.Equal("00:00:04:00", vm.InText);
        Assert.Equal("00:00:06:00", vm.OutText);
        Assert.Contains("51 cuadros", vm.ClipDurationText);
        Assert.True(vm.ExportCommand.CanExecute(null));

        vm.ClipName = "  Gol  ";
        await vm.ExportCommand.ExecuteAsync(null);
        var request = Assert.Single(clips.Requests);
        Assert.Equal(100, request.InFrame);
        Assert.Equal(151, request.OutFrame);   // el servicio recibe el siguiente al último: [100, 151)
        Assert.Equal(ClipMode.Fast, request.Mode);
        Assert.Equal("Gol", request.Name);
        Assert.Equal(Environment.UserName, request.Operator);
        Assert.False(vm.StatusIsError);
        Assert.Contains("Noticias (00.00.04-00.00.06).mp4", vm.StatusMessage);
        Assert.Matches(@"0[.,]36 s antes", vm.StatusMessage); // el aviso de la lista de edición del modo rápido
        Assert.True(vm.HasLastClip);
    }

    [Fact]
    public async Task ExactMode_SendsTheChosenEncoder()
    {
        var clips = new FakeClips();
        var vm = await OpenAsync(clips);
        vm.Position = 10; vm.MarkInCommand.Execute(null);
        vm.Position = 20; vm.MarkOutCommand.Execute(null);
        vm.Exact = true;
        Assert.False(vm.Fast);
        vm.SelectedEncoder = vm.Encoders.Single(e => e.Value == ClipVideoEncoder.ProRes);
        await vm.ExportCommand.ExecuteAsync(null);
        var request = Assert.Single(clips.Requests);
        Assert.Equal(ClipMode.Exact, request.Mode);
        Assert.Equal(ClipVideoEncoder.ProRes, request.Encoder);
        Assert.Null(request.Name);
    }

    [Fact]
    public async Task ChangingTheLanguage_RecomposesTheTexts_AndKeepsTheChoices()
    {
        var vm = await OpenAsync(new FakeClips());
        vm.SelectedEncoder = vm.Encoders.Single(e => e.Value == ClipVideoEncoder.Hevc);
        Assert.Equal("Como la grabación", vm.Encoders[0].Label);
        Localizer.Language = AppLanguage.English;
        Assert.Equal("Same as the recording", vm.Encoders[0].Label);
        Assert.Equal(ClipVideoEncoder.Hevc, vm.SelectedEncoder.Value);
        Assert.StartsWith("No re-encoding", vm.ModeHint);
        Assert.Equal("25 frames/s", vm.RateText);
        Assert.Equal("mark the in and out points", vm.ClipDurationText);
        await vm.DisposeAsync(); // suelta la suscripción al idioma
    }

    [Fact]
    public async Task AnOutBeforeTheIn_ClearsTheOtherMark()
    {
        var vm = await OpenAsync(new FakeClips());
        vm.Position = 300; vm.MarkOutCommand.Execute(null);
        vm.Position = 400; vm.MarkInCommand.Execute(null); // la entrada después de la salida: la salida ya no sirve
        Assert.Equal(400, vm.InFrame);
        Assert.Null(vm.OutFrame);
        Assert.False(vm.ExportCommand.CanExecute(null));

        vm.Position = 450; vm.MarkOutCommand.Execute(null);
        vm.Position = 200; vm.MarkInCommand.Execute(null);
        Assert.Equal(200, vm.InFrame);
        Assert.Equal(450, vm.OutFrame);
        vm.ClearMarksCommand.Execute(null);
        Assert.Null(vm.InFrame);
        Assert.Null(vm.OutFrame);
    }

    [Fact]
    public async Task SteppingAndJumping_MoveThePosition_WithinTheRecording()
    {
        var vm = await OpenAsync(new FakeClips());
        vm.StepForwardCommand.Execute(null);
        Assert.Equal(1, vm.Position);
        vm.SecondForwardCommand.Execute(null);
        Assert.Equal(26, vm.Position);
        vm.StepBackCommand.Execute(null);
        Assert.Equal(25, vm.Position);
        vm.GoToEndCommand.Execute(null);
        Assert.Equal(749, vm.Position);
        vm.StepForwardCommand.Execute(null);
        Assert.Equal(749, vm.Position); // no se sale
        vm.GoToStartCommand.Execute(null);
        vm.SecondBackCommand.Execute(null);
        Assert.Equal(0, vm.Position);
    }

    [Fact]
    public async Task WhatFails_IsToldInWords()
    {
        var clips = new FakeClips { ExportFails = new ClipException(ClipError.PiecesDiffer, "Noticias_1.mp4 ≠ Noticias_2.mp4") };
        var vm = await OpenAsync(clips);
        vm.Position = 240; vm.MarkInCommand.Execute(null);
        vm.Position = 260; vm.MarkOutCommand.Execute(null);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.True(vm.StatusIsError);
        Assert.Contains("Noticias_1.mp4 ≠ Noticias_2.mp4", vm.StatusMessage);
        Assert.Contains("por separado", vm.StatusMessage);
        Assert.False(vm.HasLastClip);

        var live = await OpenAsync(new FakeClips { LoadFails = new ClipException(ClipError.InProgress) });
        Assert.False(live.IsLoaded);
        Assert.Contains("Se está grabando", live.LoadMessage);
    }

    [Fact]
    public async Task TheWindow_SaysWhereTheSoundGoes_WhenItIsNotTheDefaultOutput()
    {
        var other = new ReviewViewModel(new FakeClips(), Session(), "A",
            _ => new ReviewPlayback(null, ReviewSound.OtherOutput, "Altavoces (NoMachine Microphone Adapter)"));
        await other.Loading;
        Assert.Contains("«Altavoces (NoMachine Microphone Adapter)»", other.PlaybackNote);
        Assert.Contains("DeckLink", other.PlaybackNote);

        var onlyDeckLink = new ReviewViewModel(new FakeClips(), Session(), "A", _ => new ReviewPlayback(null, ReviewSound.OnlyDeckLink));
        await onlyDeckLink.Loading;
        Assert.StartsWith("🔇 Sin sonido", onlyDeckLink.PlaybackNote);
        Localizer.Language = AppLanguage.English;
        Assert.StartsWith("🔇 No sound", onlyDeckLink.PlaybackNote);

        var normal = new ReviewViewModel(new FakeClips(), Session(), "A", _ => new ReviewPlayback(null));
        await normal.Loading;
        Assert.Equal("", normal.PlaybackNote);
        await other.DisposeAsync();
        await onlyDeckLink.DisposeAsync();
        await normal.DisposeAsync();
    }

    [Fact]
    public void TheRecordingLists_OfferReview_OnlyForFinishedRecordings()
    {
        var reviewed = new List<RecordingSession>();
        var finished = Session();
        var live = Session();
        live.EndedAt = null;
        var empty = Session();
        empty.Segments.Clear();

        var row = new RecordingCleanupRow(finished, "A", inProgress: false);
        Assert.True(row.CanReview);
        Assert.False(new RecordingCleanupRow(live, "A", inProgress: true).CanReview);
        Assert.False(new RecordingCleanupRow(empty, "A", inProgress: false).CanReview);

        var vm = new RecordingCleanupViewModel(new FakeSessions(), new FakeDeletion(new FakeSessions()), new FixedClock(DateTimeOffset.UtcNow),
            new Dictionary<Guid, string>(), (_, _) => false, reviewed.Add);
        Assert.True(vm.ReviewAvailable);
        vm.ReviewCommand.Execute(row);
        Assert.Same(finished, Assert.Single(reviewed));

        // Sin FFmpeg (modo demostración) no se ofrece.
        var demo = new RecordingsViewModel(new FakeSessions(), new FixedClock(DateTimeOffset.UtcNow), new Dictionary<Guid, string>());
        Assert.False(demo.ReviewAvailable);
    }
}
