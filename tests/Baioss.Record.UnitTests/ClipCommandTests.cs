using Baioss.Record.Application.Clips;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg.Clips;
using Xunit;
using static Baioss.Record.UnitTests.ClipTimelineTests;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Las líneas de FFmpeg de los clips (rápido: copia; exacto: recodificar) y del reproductor de la revisión, el corte sin huecos de
/// una copia con B-frames, el contenedor del clip y la elección del codificador del modo exacto.
/// </summary>
public sealed class ClipCommandTests
{
    private static string After(IReadOnlyList<string> args, string flag) => args[IndexOf(args, flag) + 1];
    private static int IndexOf(IReadOnlyList<string> args, string flag) => args.ToList().IndexOf(flag);

    [Fact]
    public void Fast_CopiesFromTheAbsoluteMark_WithTheIndexFirst()
    {
        var t = Timeline(FrameRate.P25, Media(0.08, 256, FrameRate.P25, formatStart: 0.058, audioStart: 0.058, bframes: true));
        var plan = ClipPlanner.Plan(t, 215, 225);
        var args = ClipCommandBuilder.Fast(plan, plan.Input.SingleFile!, end: 9.0601, ContainerFormat.Mp4, @"C:\clips\x.mp4.part", t.Rate);

        // Marca absoluta (no relativa al inicio del formato): 0,08 + 215/25, 1 ms antes.
        Assert.Equal("1", After(args, "-seek_timestamp"));
        Assert.Equal("8.679000", After(args, "-ss"));
        Assert.Equal("9.060100", After(args, "-to"));
        Assert.True(IndexOf(args, "-ss") < IndexOf(args, "-i"), "-ss y -to tienen que ir ANTES de -i (búsqueda de la entrada)");
        Assert.Equal(t.Pieces[0].FilePath, After(args, "-i"));
        Assert.Equal("copy", After(args, "-c"));
        Assert.Equal("+faststart", After(args, "-movflags"));
        Assert.Equal("mp4", After(args, "-f"));
        Assert.Equal(@"C:\clips\x.mp4.part", args[^1]);
        Assert.Contains("0:v:0?", args);
        Assert.Contains("0:a?", args);
        Assert.DoesNotContain("concat", args);
    }

    [Fact]
    public void Exact_ReEncodesJustTheSpan_WithItsMarginsUnderTheFrames()
    {
        var t = Segmented25();
        var plan = ClipPlanner.Plan(t, 209, 325);
        var encoding = ClipEncoding.Resolve(ClipVideoEncoder.H264, t.Media, null, new ClipEncoderSupport(true, true, false));
        var args = ClipCommandBuilder.Exact(plan, @"C:\tmp\list.ffconcat", encoding, @"C:\clips\x.mp4.part", t.Rate);

        Assert.Equal("8.380016", After(args, "-ss"));   // 8,381016 − 1 ms
        Assert.Equal("13.020016", After(args, "-to"));  // 13,021016 − 1 ms
        Assert.Equal("+skip_manual", After(args, "-flags2:a")); // el audio tapado por la lista de edición de cada segmento
        Assert.Equal("concat", After(args, "-f"));
        Assert.Equal("0", After(args, "-safe"));
        Assert.Equal(@"C:\tmp\list.ffconcat", After(args, "-i"));
        Assert.Equal("h264_nvenc", After(args, "-c:v"));
        Assert.Equal("0", After(args, "-bf"));          // sin B-frames: el reproductor de Windows no lee la lista de edición
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal(new[] { "-f", "mp4", "-y", @"C:\clips\x.mp4.part" }, args.Skip(args.Count - 4));
    }

    [Fact]
    public void CopyEnd_WithoutBFrames_IsTheOutMark()
    {
        // Sin B-frames, decodificación = presentación: el corte es la salida (con su margen) y no sobra nada.
        var packets = Enumerable.Range(400, 50).Select(i => (i * 0.04, i * 0.04)).ToList();
        var (end, extra) = ClipCommandBuilder.CopyEnd(packets, 17.0, FrameRate.P25);
        Assert.Equal(16.999, end, 6);
        Assert.Equal(0, extra);
    }

    [Fact]
    public void CopyEnd_WithBFrames_TakesTheFramesThatWouldBeMissing()
    {
        // GOP de x264 (I P B B P B B …): el P de 0,48 se decodifica antes que los B de 0,40 y 0,44. Con la salida en 0,40, cortar
        // por la decodificación en la salida dejaba el P (0,48) sin los B que lo preceden: el clip saltaba 0,36 → 0,48.
        var packets = new List<(double Pts, double Dts)>
        {
            (0.00, -0.08), (0.12, -0.04), (0.04, 0.00), (0.08, 0.04), (0.24, 0.08), (0.16, 0.12), (0.20, 0.16),
            (0.36, 0.20), (0.28, 0.24), (0.32, 0.28), (0.48, 0.32), (0.40, 0.36), (0.44, 0.40), (0.60, 0.44), (0.52, 0.48),
        };
        var (end, extra) = ClipCommandBuilder.CopyEnd(packets, 0.40, FrameRate.P25);
        var kept = packets.Where(p => p.Dts < end).Select(p => p.Pts).OrderBy(p => p).ToList();
        // Todo lo de antes de la salida entra, y lo que entra de más va seguido: 0,40, 0,44 y 0,48, sin huecos.
        Assert.Equal(new[] { 0.00, 0.04, 0.08, 0.12, 0.16, 0.20, 0.24, 0.28, 0.32, 0.36, 0.40, 0.44, 0.48 }, kept.Select(p => Math.Round(p, 2)));
        Assert.Equal(3, extra);
    }

    [Fact]
    public void CopyEnd_NeverCutsTheAudioBeforeTheOutMark()
    {
        // El audio se corta por el mismo umbral: aunque al vídeo le bastara cortar antes (en 0,284 entraría 0..0,36 seguido), el
        // umbral no baja de la salida, para no dejar sin el final de una frase el clip. A cambio, el vídeo llega hasta el P de 0,48.
        var packets = new List<(double Pts, double Dts)>
        {
            (0.00, -0.08), (0.12, -0.04), (0.04, 0.00), (0.08, 0.04), (0.24, 0.08), (0.16, 0.12), (0.20, 0.16),
            (0.36, 0.20), (0.28, 0.24), (0.32, 0.28), (0.48, 0.32), (0.40, 0.36), (0.44, 0.40),
        };
        var (end, extra) = ClipCommandBuilder.CopyEnd(packets, 0.36, FrameRate.P25);
        Assert.True(end >= 0.359, $"umbral {end}");
        var kept = packets.Where(p => p.Dts < end).Select(p => Math.Round(p.Pts, 2)).OrderBy(p => p).ToList();
        Assert.Equal(Enumerable.Range(0, 13).Select(i => Math.Round(i * 0.04, 2)), kept); // 0..0,48 sin huecos
        Assert.Equal(4, extra);
    }

    [Theory]
    [InlineData("h264", "aac", ".mp4", ContainerFormat.Mp4)]
    [InlineData("h264", "aac", ".mov", ContainerFormat.Mov)]  // MOV se queda en MOV
    [InlineData("h264", "aac", ".ts", ContainerFormat.Mp4)]   // H.264 + AAC caben en MP4
    [InlineData("h264", "aac", ".mkv", ContainerFormat.Mp4)]
    [InlineData("hevc", "aac", ".ts", ContainerFormat.Mp4)]
    [InlineData("h264", "pcm_s24le", ".mxf", ContainerFormat.Mov)] // PCM: MOV (el ipcm de MP4 no lo lee casi nadie)
    [InlineData("mpeg2video", "mp2", ".mpg", ContainerFormat.Mov)]
    [InlineData("prores", "pcm_s24le", ".mov", ContainerFormat.Mov)]
    [InlineData("dnxhd", "pcm_s24le", ".mxf", ContainerFormat.Mov)]
    public void TheFastClip_GoesInAContainerItsCodecsFit(string video, string audio, string ext, ContainerFormat expected)
    {
        var media = Media(0, 250, FrameRate.P25, codec: video) with
        {
            Audio = new[] { new MediaStreamInfo { Type = "audio", Codec = audio, SampleRate = 48_000, Channels = 2, Start = 0 } },
        };
        Assert.Equal(expected, ClipCommandBuilder.FastContainer(media, @"D:\G\rec" + ext));
    }

    [Theory]
    [InlineData(".wav", ContainerFormat.Wav)]
    [InlineData(".mp3", ContainerFormat.Mp3Audio)]
    [InlineData(".mp4", ContainerFormat.Mp4)]
    public void AnAudioOnlyFastClip_StaysInItsFormat(string ext, ContainerFormat expected)
    {
        var media = new MediaDescription
        {
            Audio = new[] { new MediaStreamInfo { Type = "audio", Codec = ext == ".mp3" ? "mp3" : ext == ".wav" ? "pcm_s24le" : "aac", SampleRate = 48_000, Channels = 2 } },
        };
        Assert.Equal(expected, ClipCommandBuilder.FastContainer(media, @"D:\G\rec" + ext));
    }

    [Fact]
    public void ThePlayer_DecodesBgraFromTheFrame_AndDeinterlacesInterlacedRecordings()
    {
        var t = Timeline(FrameRate.P2997, Media(0, 3000, FrameRate.P2997, fieldOrder: "tt"));
        var input = new TimelineInput(t, 0, 0);
        var args = ClipCommandBuilder.PlayerVideo(input, t.Pieces[0].FilePath, input.TimeOf(300), 1280, 720, deinterlace: true, t.Rate);
        Assert.Equal("1", After(args, "-seek_timestamp"));
        Assert.Equal("10.009000", After(args, "-ss"));   // 300 × 1001/30000 = 10,01 s − 1 ms
        Assert.StartsWith("bwdif=", After(args, "-vf"));
        Assert.Contains("scale=1280:720", After(args, "-vf"));
        Assert.Equal("passthrough", After(args, "-fps_mode"));
        Assert.Equal("bgra", After(args, "-pix_fmt"));
        Assert.Equal("rawvideo", After(args, "-f"));

        var audio = ClipCommandBuilder.PlayerAudio(input, t.Pieces[0].FilePath, input.TimeOf(300), t.Rate);
        Assert.Equal("0:a:0", After(audio, "-map"));
        Assert.Equal("s16le", After(audio, "-f"));
        Assert.Equal("+skip_manual", After(audio, "-flags2:a"));
        Assert.Equal("48000", After(audio, "-ar"));
    }

    // --- Codificador del modo exacto ---

    private static MediaDescription H264File(string pix = "yuv420p", string field = "progressive", long bitrate = 12_000_000) => new()
    {
        FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
        Video = new MediaStreamInfo
        {
            Type = "video", Codec = "h264", Profile = "High", PixelFormat = pix, Width = 1920, Height = 1080, FrameRate = FrameRate.P25,
            FieldOrder = field, BitRate = bitrate, Start = 0, Frames = 250,
        },
        Audio = new[] { new MediaStreamInfo { Index = 1, Type = "audio", Codec = "aac", SampleRate = 48_000, Channels = 2, BitRate = 192_000, Start = 0 } },
    };

    [Fact]
    public void SameAsRecording_H264_UsesNvenc_AtTheFileBitrate_WithoutBFrames()
    {
        var e = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, H264File(), null, new ClipEncoderSupport(true, true, false));
        Assert.Equal("h264_nvenc", e.VideoEncoder);
        Assert.True(e.UsesGpu);
        Assert.Equal(ContainerFormat.Mp4, e.Container);
        Assert.Equal("12000000", After(e.VideoArgs, "-b:v"));
        Assert.Equal("vbr", After(e.VideoArgs, "-rc"));
        Assert.Equal("0", After(e.VideoArgs, "-bf"));
        Assert.Equal("50", After(e.VideoArgs, "-g"));    // dos segundos a 25
        Assert.Equal("aac", After(e.AudioArgs, "-c:a"));
        Assert.Equal("192000", After(e.AudioArgs, "-b:a"));
        Assert.Null(e.VideoFilter);
    }

    [Fact]
    public void WithoutNvenc_OrWhenItFailed_TheClipIsEncodedOnTheCpu()
    {
        var cpu = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, H264File(), null, ClipEncoderSupport.CpuOnly);
        Assert.Equal("libx264", cpu.VideoEncoder);
        Assert.Equal("0", After(cpu.VideoArgs, "-bf"));
        var retry = ClipEncoding.Resolve(ClipVideoEncoder.Hevc, H264File(), null, new ClipEncoderSupport(true, true, true), forceCpu: true);
        Assert.Equal("libx265", retry.VideoEncoder);
        Assert.False(retry.UsesGpu);
    }

    [Fact]
    public void NvencCannotDo422Or10BitH264_SoThoseGoToX264WithTheirPixelFormat()
    {
        var e = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, H264File(pix: "yuv422p10le"), null, new ClipEncoderSupport(true, true, true));
        Assert.Equal("libx264", e.VideoEncoder);
        Assert.Equal("yuv422p10le", After(e.VideoArgs, "-pix_fmt"));
    }

    [Fact]
    public void AnInterlacedRecording_StaysInterlaced_WithItsFieldOrder()
    {
        var e = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, H264File(field: "tt"), null, ClipEncoderSupport.CpuOnly);
        Assert.Equal("setfield=mode=tff", e.VideoFilter);
        Assert.Equal("tt", After(e.VideoArgs, "-field_order"));
        Assert.Equal("+ilme+ildct", After(e.VideoArgs, "-flags"));
    }

    [Fact]
    public void ThePresetOfTheRecording_GivesItsEncoderSettings_WhenItIsStillThatCodec()
    {
        var preset = new RecordingProfile
        {
            Name = "H.264 Broadcast", VideoCodec = VideoCodec.H264Nvenc, RateControl = RateControlMode.ConstantBitrate,
            VideoBitrate = Bitrate.FromMbps(20), GopSize = 15, ClosedGop = true, Container = ContainerFormat.Mp4,
        };
        var e = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, H264File(bitrate: 19_500_000), preset, new ClipEncoderSupport(true, false, false));
        Assert.Equal("h264_nvenc", e.VideoEncoder);
        Assert.Equal("15", After(e.VideoArgs, "-g"));          // el GOP del preset
        Assert.Equal("19500000", After(e.VideoArgs, "-b:v"));  // la tasa del archivo
        Assert.Contains("-no-scenecut", e.VideoArgs);

        // El preset del canal cambió de códec desde que se grabó: no manda.
        preset.VideoCodec = VideoCodec.ProRes;
        var other = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, H264File(), preset, new ClipEncoderSupport(true, false, false));
        Assert.Equal("h264_nvenc", other.VideoEncoder);
        Assert.Equal("50", After(other.VideoArgs, "-g"));
    }

    [Fact]
    public void ProRes_KeepsTheProfileOfTheRecording_ButAChosenProResIs422Hq()
    {
        var prores = new MediaDescription
        {
            Video = new MediaStreamInfo
            {
                Type = "video", Codec = "prores", Profile = "4444", PixelFormat = "yuv444p12le", Width = 1920, Height = 1080,
                FrameRate = FrameRate.P25, FieldOrder = "progressive", Start = 0, Frames = 250,
            },
            Audio = new[] { new MediaStreamInfo { Type = "audio", Codec = "pcm_s24le", SampleRate = 48_000, Channels = 2, Start = 0 } },
        };
        var same = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, prores, null, ClipEncoderSupport.CpuOnly);
        Assert.Equal("prores_aw", same.VideoEncoder);
        Assert.Equal("4", After(same.VideoArgs, "-profile:v"));
        Assert.Equal(ContainerFormat.Mov, same.Container);
        Assert.Equal("pcm_s24le", After(same.AudioArgs, "-c:a"));

        var chosen = ClipEncoding.Resolve(ClipVideoEncoder.ProRes, H264File(), null, ClipEncoderSupport.CpuOnly);
        Assert.Equal("prores_aw", chosen.VideoEncoder);
        Assert.Equal("3", After(chosen.VideoArgs, "-profile:v"));
        Assert.Equal("yuv422p10le", After(chosen.VideoArgs, "-pix_fmt"));
        Assert.Equal(ContainerFormat.Mov, chosen.Container);
    }

    [Theory]
    [InlineData("DNXHR HQX", "yuv422p10le", "dnxhr_hqx")]
    [InlineData("DNXHR SQ", "yuv422p", "dnxhr_sq")]
    public void DnxHr_KeepsItsProfile(string profile, string pix, string expected)
    {
        var media = new MediaDescription
        {
            Video = new MediaStreamInfo
            {
                Type = "video", Codec = "dnxhd", Profile = profile, PixelFormat = pix, Width = 1920, Height = 1080,
                FrameRate = FrameRate.P25, FieldOrder = "progressive", Start = 0, Frames = 250,
            },
        };
        var e = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, media, null, ClipEncoderSupport.CpuOnly);
        Assert.Equal("dnxhd", e.VideoEncoder);
        Assert.Equal(expected, After(e.VideoArgs, "-profile:v"));
    }

    [Fact]
    public void ClassicDnxhd_PicksTheCidOfItsBitrate()
    {
        var media = new MediaDescription
        {
            Video = new MediaStreamInfo
            {
                Type = "video", Codec = "dnxhd", Profile = "DNXHD", PixelFormat = "yuv422p", Width = 1920, Height = 1080,
                FrameRate = FrameRate.P2997, FieldOrder = "tt", BitRate = 219_700_000, Start = 0, Frames = 300,
            },
        };
        var e = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, media, null, ClipEncoderSupport.CpuOnly);
        Assert.Equal("dnxhd", After(e.VideoArgs, "-profile:v"));
        Assert.Equal("220000000", After(e.VideoArgs, "-b:v")); // el CID 1243 (DNxHD 220, entrelazado)
        Assert.Equal("+ildct", After(e.VideoArgs, "-flags"));
    }

    [Fact]
    public void AudioOnlyRecordings_AreReEncodedInTheirFormat()
    {
        var wav = new MediaDescription
        {
            FormatName = "wav",
            Audio = new[] { new MediaStreamInfo { Type = "audio", Codec = "pcm_s16le", SampleRate = 44_100, Channels = 2 } },
        };
        var e = ClipEncoding.Resolve(ClipVideoEncoder.SameAsRecording, wav, null, ClipEncoderSupport.CpuOnly);
        Assert.Null(e.VideoEncoder);
        Assert.Equal(ContainerFormat.Wav, e.Container);
        Assert.Equal("pcm_s16le", After(e.AudioArgs, "-c:a"));
        Assert.Equal("44100", After(e.AudioArgs, "-ar"));
        Assert.Empty(e.VideoArgs);
    }

    // --- ffprobe ---

    /// <summary>Lo que dijo ffprobe (FFmpeg 8.0) del primer segmento de una grabación del muxer segment con x264.</summary>
    private const string SegmentJson = """
        {
            "programs": [ ],
            "stream_groups": [ ],
            "streams": [
                { "index": 0, "codec_name": "h264", "profile": "High", "codec_type": "video", "width": 1920, "height": 1080,
                  "has_b_frames": 2, "sample_aspect_ratio": "1:1", "pix_fmt": "yuv420p", "field_order": "progressive",
                  "r_frame_rate": "25/1", "avg_frame_rate": "25/1", "start_time": "0.080000", "duration": "10.240000",
                  "bit_rate": "71593", "nb_frames": "256" },
                { "index": 1, "codec_name": "aac", "profile": "LC", "codec_type": "audio", "sample_rate": "48000", "channels": 2,
                  "r_frame_rate": "0/0", "avg_frame_rate": "0/0", "start_time": "0.058000", "duration": "10.197333",
                  "bit_rate": "27518", "nb_frames": "478" }
            ],
            "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "start_time": "0.058000", "duration": "10.262000" }
        }
        """;

    [Fact]
    public void Ffprobe_TellsTheMarksCodecsAndFrames()
    {
        var m = FfprobeMedia.Parse(SegmentJson)!;
        Assert.Equal(0.058, m.FormatStart, 6);
        Assert.Equal("h264", m.Video!.Codec);
        Assert.Equal(new FrameRate(25, 1), m.Video.FrameRate);
        Assert.Equal(0.08, m.Video.Start!.Value, 6);
        Assert.True(m.Video.HasBFrames);
        Assert.Equal(256, FfprobeMedia.FrameCount(m, RecordingTimeline.AudioOnlyRate));
        Assert.Single(m.Audio);
        Assert.Equal(48_000, m.Audio[0].SampleRate);
        var piece = Piece(0, m);
        Assert.Equal(0.022, piece.LeadIn, 6);
    }

    [Fact]
    public void Ffprobe_WithoutFrameCount_EstimatesItFromTheDuration()
    {
        // MKV y TS no dicen nb_frames.
        var m = FfprobeMedia.Parse(SegmentJson.Replace("\"nb_frames\": \"256\"", "\"nb_frames\": \"N/A\""))!;
        Assert.Equal(256, FfprobeMedia.FrameCount(m, RecordingTimeline.AudioOnlyRate));
    }

    [Theory]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("25/1", 25, 1)]
    [InlineData("0/0", 0, 0)]
    [InlineData("90000/1", 0, 0)]
    [InlineData("N/A", 0, 0)]
    public void FrameRates_FromFfprobe(string text, int num, int den)
    {
        var rate = FfprobeMedia.Rate(text);
        if (num == 0) Assert.Null(rate);
        else Assert.Equal(new FrameRate(num, den), rate);
    }

    [Fact]
    public void Ffprobe_GarbageIsNotAFile()
    {
        Assert.Null(FfprobeMedia.Parse("{}"));
        Assert.Null(FfprobeMedia.Parse("not json"));
    }
}

/// <summary>La entrada «Clip exportado» de la actividad, en palabras, a partir del evento tal como se guarda.</summary>
[Collection("Localizer")] // el idioma es estado global
public sealed class ClipAuditTests : IDisposable
{
    private readonly Baioss.Record.Application.Localization.AppLanguage _original = Baioss.Record.Application.Localization.Localizer.Language;
    public void Dispose() => Baioss.Record.Application.Localization.Localizer.Language = _original;

    [Fact]
    public void AnExportedClip_SaysWhichFileFromWhichRecordingAndWhichSpan()
    {
        var e = new Baioss.Record.Domain.Events.ClipExported(Guid.NewGuid(), Guid.NewGuid(), "Noticias (00.12.05-00.12.35).mp4",
            "Noticias_1.mp4", TimeSpan.FromSeconds(725.4), TimeSpan.FromSeconds(755.4), ClipMode.Exact, "h264_nvenc", 43_200_000, "jcruz");
        var entry = Baioss.Record.Infrastructure.Messaging.EventLogWriter.ToEntry(e);
        Assert.Equal("ClipExported", entry.Category);
        Assert.Equal("jcruz", entry.Operator);

        Baioss.Record.Application.Localization.Localizer.Language = Baioss.Record.Application.Localization.AppLanguage.Spanish;
        var es = Baioss.Record.Application.Audit.AuditText.Detail(entry.Category, entry.PayloadJson, entry.Message);
        Assert.Equal("Clip exportado", Baioss.Record.Application.Audit.AuditText.Category(entry.Category));
        Assert.StartsWith("«Noticias (00.12.05-00.12.35).mp4» de «Noticias_1.mp4» · 00:12:05.40 → 00:12:35.40 (30 s) · exacto (h264_nvenc) · ", es);

        Baioss.Record.Application.Localization.Localizer.Language = Baioss.Record.Application.Localization.AppLanguage.English;
        var en = Baioss.Record.Application.Audit.AuditText.Detail(entry.Category, entry.PayloadJson, entry.Message);
        Assert.Contains("from «Noticias_1.mp4»", en);
        Assert.Contains("exact (h264_nvenc)", en);

        var fast = Baioss.Record.Infrastructure.Messaging.EventLogWriter.ToEntry(e with { Mode = ClipMode.Fast, Encoder = null });
        Assert.Contains("fast (copy)", Baioss.Record.Application.Audit.AuditText.Detail(fast.Category, fast.PayloadJson, fast.Message));
    }
}
