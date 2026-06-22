using Baioss.Record.Application.Channels;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Storage;
using Xunit;

namespace Baioss.Record.UnitTests;

public class FfmpegDetectParserTests
{
    [Theory]
    [InlineData("[blackdetect @ 0x55] black_start:12.34", AlarmType.VideoBlack, true)]
    [InlineData("[blackdetect @ 0x55] black_end:15.67 black_duration:3.33", AlarmType.VideoBlack, false)]
    [InlineData("[freezedetect @ 0x55] lavfi.freezedetect.freeze_start: 3.0", AlarmType.VideoFreeze, true)]
    [InlineData("[freezedetect @ 0x55] lavfi.freezedetect.freeze_end: 9.0", AlarmType.VideoFreeze, false)]
    [InlineData("[silencedetect @ 0x55] silence_start: 5.20", AlarmType.AudioSilence, true)]
    [InlineData("[silencedetect @ 0x55] silence_end: 8.10 | silence_duration: 2.90", AlarmType.AudioSilence, false)]
    public void Parse_MapsDetectLinesToTransitions(string line, AlarmType type, bool active)
    {
        var t = FfmpegDetectParser.Parse(line);
        Assert.NotNull(t);
        Assert.Equal(type, t!.Value.Type);
        Assert.Equal(active, t.Value.Active);
    }

    [Theory]
    [InlineData("frame=100 fps=50.0")]
    [InlineData("[Parsed_ebur128 @ 0x55]  FTPK: -16.6 -16.9 dBFS")]
    [InlineData("")]
    public void Parse_ReturnsNull_ForNonDetectLines(string line)
        => Assert.Null(FfmpegDetectParser.Parse(line));
}

public class DiskSpaceGuardPolicyTests
{
    private static readonly TimeSpan Warn = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Crit = TimeSpan.FromMinutes(3);
    private const long MinFree = 2L * 1024 * 1024 * 1024; // 2 GiB

    [Fact]
    public void Evaluate_PlentyOfSpace_IsOk()
    {
        long free = 500L * 1024 * 1024 * 1024; // 500 GiB
        var (level, info) = DiskSpaceGuard.Evaluate(free, free, 6_000_000, Warn, Crit, MinFree);
        Assert.Equal(DiskLevel.Ok, level);
        Assert.NotNull(info.EstimatedRemaining);
    }

    [Fact]
    public void Evaluate_LowRemainingTime_IsLow_ViaTimeRule()
    {
        // 20 MB/s · 12 GB libres ⇒ ~10 min restantes: por debajo del aviso (15 min) pero con bytes de sobra.
        long bps = 20_000_000;
        long free = bps * 600;
        var (level, _) = DiskSpaceGuard.Evaluate(free, free, bps, Warn, Crit, MinFree);
        Assert.Equal(DiskLevel.Low, level);
    }

    [Fact]
    public void Evaluate_CriticalRemainingTime_IsCritical()
    {
        // 50 MB/s · 6 GB libres ⇒ ~2 min restantes: por debajo del umbral crítico (3 min).
        long bps = 50_000_000;
        long free = bps * 120;
        var (level, _) = DiskSpaceGuard.Evaluate(free, free, bps, Warn, Crit, MinFree);
        Assert.Equal(DiskLevel.Critical, level);
    }

    [Fact]
    public void Evaluate_BelowByteFloor_IsCritical_EvenWithoutDataRate()
    {
        long free = 1L * 1024 * 1024 * 1024; // 1 GiB < piso de 2 GiB
        var (level, info) = DiskSpaceGuard.Evaluate(free, 100L * 1024 * 1024 * 1024, bytesPerSecond: 0, Warn, Crit, MinFree);
        Assert.Equal(DiskLevel.Critical, level);
        Assert.Null(info.EstimatedRemaining); // sin ritmo de datos no se estima tiempo restante
    }
}
