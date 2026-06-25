using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Recording;
using Xunit;

namespace Baioss.Record.UnitTests;

public class RecordingProfileValidatorTests
{
    private static RecordingProfile Valid() => new()
    {
        Name = "ok", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 50, RateControl = RateControlMode.ConstantBitrate,
        AudioCodec = AudioCodec.Aac, AudioBitrate = Bitrate.FromKbps(256), AudioSampleRate = 48_000,
        Container = ContainerFormat.Mp4,
    };

    [Fact]
    public void Valid_ProfileHasNoErrors() => Assert.Empty(RecordingProfileValidator.Validate(Valid()));

    [Fact]
    public void Zero_VideoBitrate_IsRejected()
    {
        var p = Valid(); p.VideoBitrate = new Bitrate(0);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Odd_Resolution_IsRejected()
    {
        var p = Valid(); p.TargetResolution = new Resolution(1281, 720);
        Assert.Contains(RecordingProfileValidator.Validate(p), e => e.Contains("PARES"));
    }

    [Fact]
    public void Zero_Gop_IsRejected()
    {
        var p = Valid(); p.GopSize = 0;
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Crf_OutOfRange_IsRejected()
    {
        var p = Valid(); p.RateControl = RateControlMode.ConstantQuality; p.Quality = 99;
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void MaxBitrate_BelowVideoBitrate_IsRejected()
    {
        var p = Valid(); p.VideoBitrate = Bitrate.FromMbps(8); p.MaxBitrate = Bitrate.FromMbps(4);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Zero_AudioBitrate_IsRejected_ForCompressedAudio()
    {
        var p = Valid(); p.AudioCodec = AudioCodec.Aac; p.AudioBitrate = new Bitrate(0);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Intra_ZeroBitrate_IsAllowed()
    {
        // ProRes va por -profile, no por -b:v: un bitrate 0 NO es un error en un códec intra.
        var p = Valid();
        p.VideoCodec = VideoCodec.ProRes; p.VideoBitrate = new Bitrate(0); p.Container = ContainerFormat.Mov;
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void ConstantQuality_ZeroBitrate_IsAllowed()
    {
        // CRF/CQ no usa -b:v: bitrate 0 es legítimo.
        var p = Valid();
        p.RateControl = RateControlMode.ConstantQuality; p.Quality = 23; p.VideoBitrate = new Bitrate(0);
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }
}
