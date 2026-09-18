using Baioss.Record.Application.Capture;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Medida del audio por canal (asistente «Medir audio»): parseo de astats y propuesta de pares.</summary>
public class AudioProbeTests
{
    private const string Sample = """
        [Parsed_astats_0 @ 000001] Channel: 1
        [Parsed_astats_0 @ 000001] Peak level dB: -8.001234
        [Parsed_astats_0 @ 000001] Channel: 2
        [Parsed_astats_0 @ 000001] Peak level dB: -8.0
        [Parsed_astats_0 @ 000001] Channel: 3
        [Parsed_astats_0 @ 000001] Peak level dB: -inf
        [Parsed_astats_0 @ 000001] Channel: 4
        [Parsed_astats_0 @ 000001] Peak level dB: -70.5
        size=N/A time=00:00:03.00 bitrate=N/A speed=1x
        """;

    [Fact]
    public void ParseAstats_ExtractsPeakPerChannelInOrder()
    {
        var peaks = FfmpegDeviceEnumerator.ParseAstatsPeaks(Sample);

        Assert.Equal(4, peaks.Count);
        Assert.Equal(-8.0, peaks[0], 2);
        Assert.Equal(-8.0, peaks[1], 2);
        Assert.Equal(double.NegativeInfinity, peaks[2]);
        Assert.Equal(-70.5, peaks[3], 2);
    }

    [Fact]
    public void ParseAstats_IgnoresOutputWithoutMeasurements()
        => Assert.Empty(FfmpegDeviceEnumerator.ParseAstatsPeaks("[decklink @ 0] Cannot enable audio input\nConversion failed!"));

    [Fact]
    public void ActivePairs_ReportsPairsWithSoundOnly()
    {
        var probe = new AudioProbe(8, new[] { -8.0, -8.0, double.NegativeInfinity, -70.5, -20.0, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity });

        Assert.Equal(new[] { 1, 3 }, probe.ActivePairs);   // par 2 (-inf/-70,5) y par 4 callados
        Assert.Equal(-8.0, probe.PairPeak(1), 2);
        Assert.Equal(-20.0, probe.PairPeak(3), 2);          // el mayor de sus dos canales
        Assert.Equal(double.NegativeInfinity, probe.PairPeak(9)); // par inexistente
    }
}
