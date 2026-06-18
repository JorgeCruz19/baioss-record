using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
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
}
