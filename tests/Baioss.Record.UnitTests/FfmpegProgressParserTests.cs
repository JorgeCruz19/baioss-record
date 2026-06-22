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
        Assert.Equal("00:00:04:00", stats.Timecode.ToString());  // out_time_us=4 s → 00:00:04:00
    }

    [Fact]
    public void Feed_Timecode_FollowsRealTimeNotFrameCount_AtHighFps()
    {
        // Regresión: a 50 fps el contador subía de dos en dos porque dividía 'frame' entre 25 fijos.
        // Ahora el HH:MM:SS viene de out_time (tiempo real): 100 cuadros a 50 fps = 2 s reales, no 4.
        var parser = new FfmpegProgressParser { NominalRate = 50 };
        parser.Feed("frame=100");
        parser.Feed("fps=50.0");
        parser.Feed("out_time_us=2000000");                      // 2 s reales
        var stats = parser.Feed("progress=continue");

        Assert.Equal("00:00:02:00", stats!.Timecode.ToString());  // 2 s, NO 4 s
        Assert.Equal(100, stats.FrameCount);                      // el conteo de cuadros sí es 100
    }

    [Fact]
    public void Feed_Timecode_UsesOutTimeMs_WhenUsAbsent()
    {
        // Algunos builds emiten solo 'out_time_ms' (que, por histórico de FFmpeg, está en microsegundos).
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=75");
        parser.Feed("out_time_ms=3000000");                      // 3 s
        var stats = parser.Feed("progress=continue");

        Assert.Equal("00:00:03:00", stats!.Timecode.ToString());
    }

    [Fact]
    public void Feed_IgnoresLinesWithoutKeyValue()
    {
        var parser = new FfmpegProgressParser();
        Assert.Null(parser.Feed("=novalue"));
        Assert.Null(parser.Feed("sinIgual"));
    }
}
