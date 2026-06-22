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

    [Theory]
    [InlineData(0L, 25, 0, 0, 0, 0)]
    [InlineData(1_000_000L, 25, 0, 0, 1, 0)]          // 1 s exacto
    [InlineData(1_500_000L, 50, 0, 0, 1, 25)]         // 1.5 s a 50 fps → 1 s + 25 cuadros
    [InlineData(3_661_000_000L, 30, 1, 1, 1, 0)]      // 1 h 1 m 1 s
    [InlineData(40_000L, 25, 0, 0, 0, 1)]             // 40 ms a 25 fps = 1 cuadro
    public void FromMicroseconds_TracksRealTime(long us, int rate, int h, int m, int s, int f)
        => Assert.Equal(new Timecode(h, m, s, f), Timecode.FromMicroseconds(us, rate));

    [Fact]
    public void FromMicroseconds_SecondsAreRateIndependent()
    {
        // El mismo tiempo real (2 s) da los MISMOS segundos a 25, 50 o 60 fps: el contador no se acelera.
        Assert.Equal(2, Timecode.FromMicroseconds(2_000_000, 25).Seconds);
        Assert.Equal(2, Timecode.FromMicroseconds(2_000_000, 50).Seconds);
        Assert.Equal(2, Timecode.FromMicroseconds(2_000_000, 60).Seconds);
    }

    [Fact]
    public void FromMicroseconds_ClampsFramesAndHandlesGuards()
    {
        Assert.Equal(new Timecode(0, 0, 0, 0), Timecode.FromMicroseconds(-5, 25));   // negativo → 0
        Assert.Equal(new Timecode(0, 0, 1, 0), Timecode.FromMicroseconds(1_000_000, 0)); // tasa 0 → 25 por defecto
    }

    [Fact]
    public void ToString_RoundTripsThroughParse()
    {
        var tc = new Timecode(10, 20, 30, 12);
        Assert.Equal(tc, Timecode.Parse(tc.ToString()));
    }
}
