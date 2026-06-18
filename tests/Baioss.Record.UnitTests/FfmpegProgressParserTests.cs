using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

public class FfmpegProgressParserTests
{
    [Fact]
    public void Feed_ReturnsNull_UntilProgressLineClosesBlock()
    {
        var parser = new FfmpegProgressParser();
        Assert.Null(parser.Feed("frame=100"));
        Assert.Null(parser.Feed("fps=25.0"));
        Assert.Null(parser.Feed("bitrate=2046.7kbits/s"));
        Assert.NotNull(parser.Feed("progress=continue"));
    }

    [Fact]
    public void Feed_ParsesAFullProgressBlock()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=100");
        parser.Feed("fps=25.0");
        parser.Feed("drop_frames=2");
        parser.Feed("dup_frames=1");
        parser.Feed("bitrate=2046.7kbits/s");
        parser.Feed("out_time_us=4000000");
        var stats = parser.Feed("progress=continue");

        Assert.NotNull(stats);
        Assert.Equal(100, stats!.FrameCount);
        Assert.Equal(25.0, stats.OutputFps);
        Assert.Equal(2, stats.DroppedFrames);
        Assert.Equal(1, stats.DuplicatedFrames);
        Assert.Equal(2_046_700, stats.Bitrate.BitsPerSecond);   // "2046.7kbits/s" → bps
        Assert.Equal("00:00:04:00", stats.Timecode.ToString());  // 100 frames @ 25 fps
    }

    [Fact]
    public void Feed_IgnoresLinesWithoutKeyValue()
    {
        var parser = new FfmpegProgressParser();
        Assert.Null(parser.Feed("=novalue"));
        Assert.Null(parser.Feed("sinIgual"));
    }
}
