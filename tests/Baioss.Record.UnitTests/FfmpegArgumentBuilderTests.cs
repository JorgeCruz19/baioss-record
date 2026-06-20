using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.UnitTests.Fakes;
using Xunit;

namespace Baioss.Record.UnitTests;

public class FfmpegArgumentBuilderTests
{
    private static (string Joined, string OutputFile) Build(RecordingProfile profile)
    {
        var builder = new FfmpegArgumentBuilder()
            .From(new FakeCaptureSource("C:/clips/in.mp4"))
            .Using(profile)
            .ForChannel("TST")
            .ToDirectory("C:/out")
            .ProxyToDirectory("C:/proxy");
        var args = builder.Build();
        return (string.Join(' ', args), builder.OutputFilePath);
    }

    private static RecordingProfile SoftwareMp4() => new()
    {
        Name = "x264", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 50,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    private static RecordingProfile ProResMov(EncoderProfile profile, PixelFormat pix = PixelFormat.Auto) => new()
    {
        Name = "prores", VideoCodec = VideoCodec.ProRes, HwAccel = HwAccel.None, GopSize = 1,
        EncoderProfile = profile, PixelFormat = pix,
        AudioCodec = AudioCodec.Pcm, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mov,
    };

    private static RecordingProfile DnxhrMov(EncoderProfile profile, PixelFormat pix = PixelFormat.Auto) => new()
    {
        Name = "dnxhr", VideoCodec = VideoCodec.DnxHr, HwAccel = HwAccel.None, GopSize = 1,
        EncoderProfile = profile, PixelFormat = pix,
        AudioCodec = AudioCodec.Pcm, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mov,
    };

    [Fact]
    public void Build_SoftwareMp4_UsesLibx264AndAac()
    {
        var (joined, outFile) = Build(SoftwareMp4());

        Assert.Contains("-i C:/clips/in.mp4", joined);
        Assert.Contains("-c:v libx264", joined);
        Assert.Contains("-c:a aac", joined);
        Assert.Contains("-ac 2", joined);                       // estéreo
        Assert.Contains("ebur128=peak=true", joined);           // metering para VU
        Assert.Contains("-movflags +faststart", joined);        // MP4
        Assert.EndsWith(".mp4", outFile);
        Assert.Contains("TST_", outFile);                       // patrón con la clave de canal
    }

    [Fact]
    public void Build_NvencProfile_EmitsGpuEncoderAndHwAccel()
    {
        var profile = SoftwareMp4();
        profile.VideoCodec = VideoCodec.H264Nvenc;
        profile.HwAccel = HwAccel.Nvenc;

        var (joined, _) = Build(profile);

        Assert.Contains("-c:v h264_nvenc", joined);
        Assert.Contains("-hwaccel cuda", joined);
        Assert.Contains("-preset p5", joined);                  // preajuste NVENC
    }

    [Fact]
    public void Build_WithTargetResolution_AddsScaleFilter()
    {
        var profile = SoftwareMp4();
        profile.TargetResolution = new Resolution(640, 360);

        var (joined, _) = Build(profile);

        Assert.Contains("-filter_complex", joined);
        Assert.Contains("scale=640:360", joined);
    }

    [Fact]
    public void Build_WithoutTargetResolution_DoesNotScale()
    {
        var (joined, _) = Build(SoftwareMp4());
        Assert.DoesNotContain("scale=", joined);
    }

    [Fact]
    public void Build_Interlaced_SetsFieldOrderAndFlags()
    {
        var profile = SoftwareMp4();
        profile.ScanType = ScanType.InterlacedTff;

        var (joined, _) = Build(profile);

        Assert.Contains("-field_order tt", joined);
        Assert.Contains("+ilme+ildct", joined);
    }

    [Fact]
    public void Build_Progressive_HasNoFieldOrder()
        => Assert.DoesNotContain("-field_order", Build(SoftwareMp4()).Joined);

    [Fact]
    public void Build_WithOutputFrameRate_AddsRateOption()
    {
        var profile = SoftwareMp4();
        profile.OutputFrameRate = FrameRate.P2997; // 30000/1001

        var (joined, _) = Build(profile);

        Assert.Contains("-r 30000/1001", joined);
    }

    [Fact]
    public void Build_ConstantQuality_UsesCrfNotBitrate()
    {
        var profile = SoftwareMp4();
        profile.RateControl = RateControlMode.ConstantQuality;
        profile.Quality = 20;

        var (joined, _) = Build(profile);

        Assert.Contains("-crf 20", joined);
        Assert.DoesNotContain("-b:v", joined);
    }

    [Fact]
    public void Build_ConstantBitrate_ConstrainsRate()
    {
        var (joined, _) = Build(SoftwareMp4()); // CBR por defecto
        Assert.Contains("-maxrate", joined);
        Assert.Contains("-bufsize", joined);
    }

    [Fact]
    public void Build_PcmInMp4_PromotesAudioToAac()
    {
        var profile = SoftwareMp4();
        profile.AudioCodec = AudioCodec.Pcm;                    // PCM no es estándar en MP4

        var (joined, _) = Build(profile);

        Assert.Contains("-c:a aac", joined);                    // promovido automáticamente
        Assert.DoesNotContain("pcm_s24le", joined);
    }

    // --- Familias ProRes / DNxHR (edición) ---

    [Fact]
    public void Build_ProRes4444_UsesProfile4AndYuv444()
    {
        var (joined, outFile) = Build(ProResMov(EncoderProfile.ProRes4444));

        Assert.Contains("-c:v prores_ks", joined);
        Assert.Contains("-profile:v 4", joined);                // 4 = ProRes 4444
        Assert.Contains("-pix_fmt yuv444p10le", joined);        // 4:4:4 obligatorio para 4444
        Assert.EndsWith(".mov", outFile);
    }

    [Fact]
    public void Build_ProResAuto_DefaultsTo422Hq()
        => Assert.Contains("-profile:v 3", Build(ProResMov(EncoderProfile.Auto)).Joined); // 3 = 422 HQ

    [Theory]
    [InlineData(EncoderProfile.ProResProxy, "0")]
    [InlineData(EncoderProfile.ProResLt, "1")]
    [InlineData(EncoderProfile.ProResStandard, "2")]
    [InlineData(EncoderProfile.ProRes4444Xq, "5")]
    public void Build_ProResFamily_MapsProfileNumber(EncoderProfile profile, string expected)
        => Assert.Contains($"-profile:v {expected}", Build(ProResMov(profile)).Joined);

    [Fact]
    public void Build_DnxHrHqx_Uses10BitProfile()
    {
        var (joined, _) = Build(DnxhrMov(EncoderProfile.DnxHrHqx));

        Assert.Contains("-c:v dnxhd", joined);
        Assert.Contains("-profile:v dnxhr_hqx", joined);
        Assert.Contains("-pix_fmt yuv422p10le", joined);
    }

    [Fact]
    public void Build_DnxHr444_Uses444ProfileAndPixelFormat()
    {
        var (joined, _) = Build(DnxhrMov(EncoderProfile.DnxHr444));

        Assert.Contains("-profile:v dnxhr_444", joined);
        Assert.Contains("-pix_fmt yuv444p10le", joined);
    }

    [Theory]
    [InlineData(EncoderProfile.DnxHrLb, "dnxhr_lb")]
    [InlineData(EncoderProfile.DnxHrSq, "dnxhr_sq")]
    [InlineData(EncoderProfile.DnxHrHq, "dnxhr_hq")]
    public void Build_DnxHrFamily_MapsProfileName(EncoderProfile profile, string expected)
        => Assert.Contains($"-profile:v {expected}", Build(DnxhrMov(profile)).Joined);

    // --- Pipeline en vivo: preview + grabación en un solo proceso ---

    private static string BuildLive(RecordingProfile profile, bool recording, bool hasAudio = true)
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4");
        // La fuente declara (o no) pista de audio: un dispositivo solo-vídeo (cámara/OBS) no tiene
        // audio que medir ni grabar, y el builder debe omitir esas salidas en consecuencia.
        source.Emit(new SignalInfo(SignalState.Locked, new Resolution(1920, 1080), new FrameRate(25, 1),
            hasAudio ? AudioLayout.Stereo : null, HasAudio: hasAudio, Timecode: null, Bitrate: null));
        var b = new FfmpegArgumentBuilder()
            .From(source)
            .Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .WithPreviewSink("tcp://127.0.0.1:9001");
        return string.Join(' ', b.BuildLive(recording, 640, 360));
    }

    [Fact]
    public void BuildLive_PreviewOnly_HasPreviewAndMetersButNoEncoder()
    {
        var joined = BuildLive(SoftwareMp4(), recording: false);

        Assert.Contains("-progress pipe:1", joined);                        // telemetría/watchdog del supervisor
        Assert.Contains("[0:v]scale=640:360,format=bgra[pv]", joined);      // rama de preview
        Assert.Contains("-map [pv] -f rawvideo tcp://127.0.0.1:9001", joined);
        Assert.Contains("ebur128=peak=true", joined);                       // medidores
        Assert.DoesNotContain("-c:v libx264", joined);                      // idle: NO graba
        Assert.DoesNotContain("-y", joined);                                // …ni escribe archivo
        Assert.DoesNotContain("TST_", joined);                              // (in.mp4 de entrada sí lleva .mp4)
    }

    [Fact]
    public void BuildLive_Recording_SplitsToPreviewAndFileAtOnce()
    {
        var joined = BuildLive(SoftwareMp4(), recording: true);

        Assert.Contains("split=2[vrec][vprev]", joined);                    // una apertura → dos ramas
        Assert.Contains("[vprev]scale=640:360,format=bgra[pv]", joined);    // preview…
        Assert.Contains("-map [pv] -f rawvideo tcp://127.0.0.1:9001", joined);
        Assert.Contains("-c:v libx264", joined);                            // …y grabación a la vez
        Assert.Contains("-movflags +faststart", joined);
        Assert.Contains("-y", joined);                                      // archivo de salida
        Assert.Contains("TST_", joined);                                    // nombre de la grabación
        Assert.Contains("ebur128=peak=true", joined);
    }

    [Fact]
    public void BuildLive_PreviewOnly_NoAudioSource_KeepsPreviewButOmitsMeter()
    {
        var joined = BuildLive(SoftwareMp4(), recording: false, hasAudio: false);

        // El preview sigue funcionando (es la regresión que reportó el usuario con OBS solo-vídeo)…
        Assert.Contains("[0:v]scale=640:360,format=bgra[pv]", joined);
        Assert.Contains("-map [pv] -f rawvideo tcp://127.0.0.1:9001", joined);
        // …pero no se pide medición ni un output solo-audio sin streams (que abortaría todo FFmpeg).
        Assert.DoesNotContain("ebur128", joined);
        Assert.DoesNotContain("-f null", joined);
        Assert.DoesNotContain("0:a:0?", joined);
    }

    [Fact]
    public void BuildLive_Recording_NoAudioSource_RecordsVideoOnly()
    {
        var joined = BuildLive(SoftwareMp4(), recording: true, hasAudio: false);

        Assert.Contains("split=2[vrec][vprev]", joined);    // sigue bifurcando a preview + grabación
        Assert.Contains("-c:v libx264", joined);            // graba vídeo…
        Assert.Contains("-y", joined);
        Assert.DoesNotContain("-c:a", joined);              // …sin pista ni códec de audio
        Assert.DoesNotContain("0:a:0?", joined);
        Assert.DoesNotContain("ebur128", joined);
    }
}
