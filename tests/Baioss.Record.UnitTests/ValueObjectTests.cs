using Baioss.Record.Domain.ValueObjects;
using Xunit;

namespace Baioss.Record.UnitTests;

public class ResolutionTests
{
    [Fact]
    public void ToString_IsWidthByHeight()
        => Assert.Equal("1280x720", new Resolution(1280, 720).ToString());

    [Fact]
    public void AspectRatio_Is16By9_ForHd1080()
        => Assert.Equal(16.0 / 9.0, Resolution.Hd1080.AspectRatio, 3);

    [Fact]
    public void AspectRatio_IsZero_WhenHeightZero()
        => Assert.Equal(0, new Resolution(1920, 0).AspectRatio);
}

public class FrameRateTests
{
    [Fact]
    public void Value_PreservesNtscFraction()
        => Assert.Equal(30000.0 / 1001.0, FrameRate.P2997.Value, 5);

    [Theory]
    [InlineData(30000, 1001, true)]   // 29.97 → candidato drop-frame
    [InlineData(60000, 1001, true)]   // 59.94
    [InlineData(25, 1, false)]
    [InlineData(30, 1, false)]
    public void IsDropFrameCandidate_DependsOnDenominator(int num, int den, bool expected)
        => Assert.Equal(expected, new FrameRate(num, den).IsDropFrameCandidate);

    [Fact]
    public void ToString_FormatsFps()
        => Assert.Equal("29.97 fps", FrameRate.P2997.ToString());
}

public class BitrateTests
{
    [Fact]
    public void FromMbps_ConvertsToBitsPerSecond()
        => Assert.Equal(8_000_000, Bitrate.FromMbps(8).BitsPerSecond);

    [Fact]
    public void FromKbps_ConvertsToBitsPerSecond()
        => Assert.Equal(256_000, Bitrate.FromKbps(256).BitsPerSecond);

    [Fact]
    public void ToString_UsesMbps_WhenAtLeastOne()
        => Assert.Equal("8 Mbps", Bitrate.FromMbps(8).ToString());

    [Fact]
    public void ToString_UsesKbps_WhenBelowOneMbps()
        => Assert.Equal("256 kbps", Bitrate.FromKbps(256).ToString());
}

public class TimecodeTests
{
    [Fact]
    public void Parse_NonDropFrame()
    {
        var tc = Timecode.Parse("01:02:03:04");
        Assert.Equal(new Timecode(1, 2, 3, 4), tc);
        Assert.False(tc.DropFrame);
    }

    [Fact]
    public void Parse_DropFrame_DetectedBySemicolon()
    {
        var tc = Timecode.Parse("01:02:03;04");
        Assert.True(tc.DropFrame);
    }

    [Fact]
    public void Parse_Invalid_Throws()
        => Assert.Throws<FormatException>(() => Timecode.Parse("01:02:03"));

    [Theory]
    [InlineData(0, 25, 0, 0, 0, 0)]
    [InlineData(25, 25, 0, 0, 1, 0)]
    [InlineData(50, 25, 0, 0, 2, 0)]
    [InlineData(1500, 25, 0, 1, 0, 0)]   // 60 s
    public void FromFrameNumber_ConvertsToHmsf(long frame, int rate, int h, int m, int s, int f)
        => Assert.Equal(new Timecode(h, m, s, f), Timecode.FromFrameNumber(frame, rate));

    [Fact]
    public void ToString_RoundTripsThroughParse()
    {
        var tc = new Timecode(10, 20, 30, 12);
        Assert.Equal(tc, Timecode.Parse(tc.ToString()));
    }
}
