using Baioss.Record.Application.Clips;
using Baioss.Record.Domain.ValueObjects;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// La línea de tiempo de una grabación en cuadros y el plan de corte de un clip entre sus piezas. Las marcas de las piezas son
/// las que dio ffprobe de los segmentos de una grabación real del muxer <c>segment</c> (25 fps, AAC): el primer segmento con el
/// vídeo a 21 ms del origen (el cebado del AAC) y los demás a 0; con B-frames (x264), el formato empieza a 58 ms y el vídeo a 80.
/// Los instantes que se esperan son los que dejaron el corte EXACTO en el cuadro pedido con el FFmpeg incluido (medido el
/// 2026-10-23; ver ClipExportTests en la suite de integración).
/// </summary>
public sealed class ClipTimelineTests
{
    internal static MediaDescription Media(double videoStart, long frames, FrameRate rate, double formatStart = 0,
        double? audioStart = 0, double? audioDuration = null, string codec = "h264", int width = 1920, int height = 1080,
        string? fieldOrder = "progressive", bool bframes = false) => new()
    {
        FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
        FormatStart = formatStart,
        FormatDuration = videoStart - formatStart + frames / rate.Value,
        Video = new MediaStreamInfo
        {
            Type = "video", Codec = codec, Width = width, Height = height, PixelFormat = "yuv420p", FrameRate = rate, FieldOrder = fieldOrder,
            Start = videoStart, Duration = frames / rate.Value, Frames = frames, HasBFrames = bframes, BitRate = 8_000_000,
        },
        Audio = audioStart is null ? Array.Empty<MediaStreamInfo>() : new[]
        {
            new MediaStreamInfo
            {
                Index = 1, Type = "audio", Codec = "aac", SampleRate = 48_000, Channels = 2, Start = audioStart,
                Duration = audioDuration ?? frames / rate.Value, BitRate = 192_000,
            },
        },
    };

    internal static TimelinePiece Piece(int index, MediaDescription media, string? path = null) => new()
    {
        SegmentId = Guid.NewGuid(), Index = index, FilePath = path ?? $@"D:\Grabaciones\Noticias_{index + 1}.mp4",
        Frames = media.Video?.Frames ?? 0, Media = media,
    };

    internal static RecordingTimeline Timeline(FrameRate rate, params MediaDescription[] pieces)
        => new(Guid.NewGuid(), Guid.NewGuid(), rate, pieces.Select((m, i) => Piece(i, m)).ToList());

    /// <summary>Tres segmentos de 10 s del muxer segment con NVENC (sin B-frames), como los medidos.</summary>
    internal static RecordingTimeline Segmented25() => Timeline(FrameRate.P25,
        Media(0.021016, 250, FrameRate.P25, audioStart: 0, audioDuration: 10.026667),
        Media(0, 250, FrameRate.P25, audioStart: 0.005, audioDuration: 10.005333),
        Media(0, 250, FrameRate.P25, audioStart: 0.010, audioDuration: 9.989333));

    [Theory]
    [InlineData(25, 1)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    [InlineData(50, 1)]
    public void EveryFrame_HasItsOwnTimecode_AndItParsesBack(int num, int den)
    {
        var rate = new FrameRate(num, den);
        var t = Timeline(rate, Media(0, 200_000, rate));
        var seen = new HashSet<string>();
        foreach (long f in Enumerable.Range(0, 5000).Select(i => (long)i).Concat(new long[] { 107_891, 107_892, 199_999 }))
        {
            var tc = t.Timecode(f);
            Assert.True(seen.Add(tc), $"{tc} se repite");
            Assert.True(t.TryParse(tc, end: false, out var back), tc);
            Assert.Equal(f, back);
            Assert.True(t.TryParse(tc, end: true, out var asEnd), tc);
            Assert.Equal(f, asEnd); // un timecode es su cuadro también como final (el cuadro que ya no entra)
        }
    }

    [Fact]
    public void Timecode_KeepsRealTime_At2997()
    {
        // Una hora a 29,97 son 107 892,1 cuadros: el primero que empieza en la hora (el 107 893) dice 01:00:00:00, no
        // 01:00:03:18 (lo que contaría un timecode a 30 sin drop-frame, que se adelanta 3,6 s por hora).
        var t = Timeline(FrameRate.P2997, Media(0, 200_000, FrameRate.P2997));
        Assert.Equal("01:00:00:00", t.Timecode(107_893));
        Assert.Equal("00:59:59:29", t.Timecode(107_892));
        Assert.Equal("00:00:01:00", t.Timecode(30));
        Assert.Equal("00:00:00:29", t.Timecode(29));
    }

    [Theory]
    [InlineData("12.36", false, 309)]       // segundos: el cuadro que se ve en ese instante
    [InlineData("12.37", false, 309)]
    [InlineData("12.36", true, 309)]        // como final: el primero que empieza en él o después
    [InlineData("12.37", true, 310)]
    [InlineData("00:00:12.360", false, 309)]
    [InlineData("0:12.36", false, 309)]
    [InlineData("00:00:12:09", false, 309)] // timecode
    [InlineData("00:00:12;09", false, 309)]
    [InlineData("17", true, 425)]
    public void Positions_AreReadAsFrames(string text, bool end, long frame)
    {
        var t = Timeline(FrameRate.P25, Media(0, 750, FrameRate.P25));
        Assert.True(t.TryParse(text, end, out var parsed));
        Assert.Equal(frame, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("00:61:00")]
    [InlineData("00:00:01:25")]   // un segundo a 25 fps tiene los cuadros 0..24
    [InlineData("1:2:3:4:5")]
    public void BadPositions_AreRejected(string text)
    {
        var t = Timeline(FrameRate.P25, Media(0, 750, FrameRate.P25));
        Assert.False(t.TryParse(text, end: false, out _));
    }

    [Fact]
    public void TimeAndFrame_RoundTrip_At2997()
    {
        var t = Timeline(FrameRate.P2997, Media(0, 10_000_000, FrameRate.P2997));
        foreach (long f in new long[] { 0, 1, 29, 30, 1001, 107_892, 2_589_407, 9_999_999 })
        {
            Assert.Equal(f, t.FrameAt(t.TimeOf(f)));
            Assert.Equal(f, t.FrameFrom(t.TimeOf(f)));
            Assert.Equal(f + 1, t.FrameFrom(t.TimeOf(f) + TimeSpan.FromTicks(10))); // 1 µs después ya es el siguiente
        }
    }

    [Fact]
    public void Locate_FindsThePieceOfEachFrame_AndSkipsEmptyPieces()
    {
        var t = Timeline(FrameRate.P25, Media(0, 250, FrameRate.P25), Media(0, 0, FrameRate.P25), Media(0, 100, FrameRate.P25));
        Assert.Equal(350, t.TotalFrames);
        Assert.Equal((0, 249L), t.Locate(249));
        Assert.Equal((2, 0L), t.Locate(250)); // la pieza vacía no contiene nada
        Assert.Equal((2, 99L), t.Locate(349));
    }

    // --- Plan de corte ---

    [Fact]
    public void ASinglePieceClip_UsesTheFileItself_WithItsOwnMarks()
    {
        // Con B-frames, el primer cuadro del archivo está en 0,08 s: el cuadro 215 del segmento, en 0,08 + 215/25.
        var t = Timeline(FrameRate.P25, Media(0.08, 256, FrameRate.P25, formatStart: 0.058, audioStart: 0.058, bframes: true));
        var plan = ClipPlanner.Plan(t, 215, 225);
        Assert.False(plan.Input.IsConcat);
        Assert.Equal(t.Pieces[0].FilePath, plan.Input.SingleFile);
        Assert.Equal(8.68, plan.Start, 6);
        Assert.Equal(9.08, plan.End, 6);
        Assert.Equal(10, plan.Frames);
    }

    [Fact]
    public void AClipAcrossSegments_JoinsThemWithConcat_AndLandsOnTheFrameGrid()
    {
        var t = Segmented25();
        var plan = ClipPlanner.Plan(t, 209, 325);
        Assert.True(plan.Input.IsConcat);
        Assert.Equal(2, plan.Input.Entries.Count);
        // El primer archivo se declara hasta el final de su vídeo (21 ms de entrada + 10 s); el segundo, hasta el final de lo suyo.
        Assert.Equal(10.021016, plan.Input.Entries[0].Duration, 6);
        // Cuadro 209: 21 ms + 209/25 (lo que dejó el exacto empezando en el 209); 325: el 75 del 2.º segmento, a 10,021016 + 3.
        Assert.Equal(8.381016, plan.Start, 6);
        Assert.Equal(13.021016, plan.End, 6);
        // De una marca de ffprobe del 2.º archivo a la escala del concat: + 10,021016 − su origen (0).
        Assert.Equal(13.021016, plan.Input.ToInputTime(1, 3.0), 6);
    }

    [Fact]
    public void AClipStartingInTheSecondSegment_StartsTheListThere()
    {
        var t = Segmented25();
        var plan = ClipPlanner.Plan(t, 470, 530);
        Assert.Equal(1, plan.Input.FirstPiece);
        Assert.Equal(2, plan.Input.LastPiece);
        Assert.Equal(8.8, plan.Start, 6);   // el 220 del 2.º segmento (medido: 470..529 exactos)
        Assert.Equal(11.2, plan.End, 6);
    }

    [Fact]
    public void TheDeclaredDuration_DoesNotLetTheAudioOverlapTheNextPiece()
    {
        // Pieza cuyo audio sigue 0,2 s después de su vídeo y la siguiente empieza con el audio en 0. El vídeo pediría declarar
        // 10 s; el audio, 10,2: se declara 10,2 (un hueco en el vídeo antes que dos audios a la vez). Menos de un paquete de AAC
        // de solape (lo de los segmentos del muxer segment: <1 ms) no cuenta: manda la rejilla de cuadros (ver Segmented25).
        var t = Timeline(FrameRate.P25,
            Media(0, 250, FrameRate.P25, audioStart: 0, audioDuration: 10.2),
            Media(0, 250, FrameRate.P25, audioStart: 0));
        var input = new TimelineInput(t, 0, 1);
        Assert.Equal(10.2, input.Entries[0].Duration, 6);
        Assert.Equal(10.2, input.TimeOf(250), 6);
    }

    [Fact]
    public void PiecesWithOtherParameters_CannotBeJoined()
    {
        var t = Timeline(FrameRate.P25, Media(0, 250, FrameRate.P25), Media(0, 250, FrameRate.P25, width: 1280, height: 720));
        var ex = Assert.Throws<ClipException>(() => ClipPlanner.Plan(t, 200, 300));
        Assert.Equal(ClipError.PiecesDiffer, ex.Error);
        // Dentro de cada pieza sí se puede cortar.
        Assert.False(ClipPlanner.Plan(t, 260, 300).Input.IsConcat);
    }

    [Theory]
    [InlineData(-1, 10, ClipError.OutOfRange)]
    [InlineData(700, 800, ClipError.OutOfRange)]
    [InlineData(750, 751, ClipError.OutOfRange)]
    [InlineData(10, 10, ClipError.Empty)]
    [InlineData(20, 10, ClipError.Empty)]
    public void ImpossibleClips_AreRefused(long from, long to, ClipError error)
    {
        var ex = Assert.Throws<ClipException>(() => ClipPlanner.Plan(Segmented25(), from, to));
        Assert.Equal(error, ex.Error);
    }

    [Fact]
    public void TheWholeRecordingClip_EndsAtTheLastFrame()
    {
        var t = Segmented25();
        var plan = ClipPlanner.Plan(t, 0, t.TotalFrames);
        Assert.Equal(0.021016, plan.Start, 6);
        Assert.Equal(0.021016 + 30, plan.End, 6);
    }

    [Fact]
    public void TheConcatList_QuotesEveryPath_AndDeclaresEachDuration()
    {
        var t = new RecordingTimeline(Guid.NewGuid(), Guid.NewGuid(), FrameRate.P25, new[]
        {
            Piece(0, Media(0.021016, 250, FrameRate.P25), @"D:\Graba ciones\L'Hora_1.mp4"),
            Piece(1, Media(0, 250, FrameRate.P25), @"D:\Graba ciones\L'Hora_2.mp4"),
        });
        var list = new TimelineInput(t, 0, 1).ConcatList();
        Assert.Equal("ffconcat version 1.0\n" +
                     "file 'D:\\Graba ciones\\L'\\''Hora_1.mp4'\nduration 10.021016\n" +
                     "file 'D:\\Graba ciones\\L'\\''Hora_2.mp4'\nduration 10.000000\n", list);
    }

    [Fact]
    public void WallClock_OfAFrame_IsItsPieceStartPlusItsTime()
    {
        var started = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);
        var t = new RecordingTimeline(Guid.NewGuid(), Guid.NewGuid(), FrameRate.P25, new[]
        {
            Piece(0, Media(0, 250, FrameRate.P25)) with { StartedAt = started },
            // Pieza tras una caída: empezó 7 s después del final de la anterior.
            Piece(1, Media(0, 250, FrameRate.P25)) with { StartedAt = started.AddSeconds(17) },
        });
        Assert.Equal(started.AddSeconds(5), t.WallClockOf(125));
        Assert.Equal(started.AddSeconds(17).AddSeconds(2), t.WallClockOf(300));
    }

    // --- Nombres ---

    [Theory]
    [InlineData(@"D:\G\Noticias_1.mp4", 3, "Noticias")]
    [InlineData(@"D:\G\Noticias_1.mp4", 1, "Noticias_1")]          // un solo archivo: el número es del nombre
    [InlineData(@"D:\G\A_20261021_200000_001.mp4", 4, "A_20261021_200000")]
    [InlineData(@"D:\G\Partido.mov", 2, "Partido")]
    [InlineData(null, 0, "clip")]
    public void TheRecordingName_DropsTheSegmentNumber(string? path, int files, string expected)
        => Assert.Equal(expected, ClipNaming.RecordingName(path, files));

    [Fact]
    public void ClipNames_AreReadable_AndValidOnWindows()
    {
        Assert.Equal("Noticias (00.12.05-00.12.35)",
            ClipNaming.ClipName("Noticias", TimeSpan.FromSeconds(725.4), TimeSpan.FromSeconds(755.9)));
        Assert.Equal("Noticias (01.00.00-01.00.10)", ClipNaming.ClipName("Noticias", TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromSeconds(10)));
        Assert.Equal("a_b_c", ClipNaming.Sanitize("a/b:c"));
        Assert.Equal("_.._x", ClipNaming.Sanitize(@"\..\x"));
        Assert.Null(ClipNaming.Sanitize(" . . "));
        Assert.Null(ClipNaming.Sanitize("///"));
        Assert.True(ClipNaming.Sanitize(new string('x', 300))!.Length <= ClipNaming.MaxLength);
    }

    [Fact]
    public void UniquePath_NeverOverwrites_NorTakesTheNameOfAClipBeingWritten()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\clips\N.mp4", @"C:\clips\N 2.mp4" + ClipNaming.PartSuffix };
        bool Exists(string p) => taken.Contains(p) || taken.Contains(p + ClipNaming.PartSuffix);
        Assert.Equal(@"C:\clips\N 3.mp4", ClipNaming.UniquePath(@"C:\clips", "N", "mp4", Exists));
        Assert.Equal(@"C:\clips\M.mov", ClipNaming.UniquePath(@"C:\clips", "M", "mov", Exists));
    }

    [Fact]
    public void Positions_InTheClipName_AreTheSecondsOfTheFrames()
    {
        var t = Timeline(FrameRate.P2997, Media(0, 10_000, FrameRate.P2997));
        // El cuadro 120 a 29,97 empieza a los 4,004 s; el 300, a los 10,01.
        Assert.Equal("x (00.00.04-00.00.10)", ClipNaming.ClipName("x", t.TimeOf(120), t.TimeOf(300)));
    }

    [Fact]
    public void Signature_DependsOnTheCodecParameters_NotOnTheMarks()
    {
        var a = Media(0.021, 250, FrameRate.P25);
        var b = Media(0, 300, FrameRate.P25, audioStart: 0.005);
        Assert.Equal(a.Signature, b.Signature);
        Assert.NotEqual(a.Signature, Media(0, 250, FrameRate.P2997).Signature);
        Assert.NotEqual(a.Signature, Media(0, 250, FrameRate.P25, codec: "hevc").Signature);
        Assert.NotEqual(a.Signature, Media(0, 250, FrameRate.P25, fieldOrder: "tt").Signature);
        Assert.NotEqual(a.Signature, Media(0, 250, FrameRate.P25, audioStart: null).Signature);
    }
}
