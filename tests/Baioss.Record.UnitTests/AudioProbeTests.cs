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

    // Salida REAL de una DeckLink Duo 2 con -channels 16 (2026-09-22), tal como llega por la TUBERÍA: la línea de progreso
    // de FFmpeg termina en retorno de carro y se pega a «Channel: 1» (en consola se vio «Channel: 12x elapsed=…»).
    // Pares con sonido: 1-2, 3-4, 5-6 (el 6 casi mudo), 7-8 y 9-10; del 11 al 16, silencio digital.
    private const string Duo2Sixteen =
        "[in#0 @ 000001932de07440] Found Decklink mode 1920 x 1080 with rate 29.97(i)\n" +
        "[aist#0:0/pcm_s16le @ 000001932e172540] Guessed Channel Layout: 9.1.6\n" +
        "size=N/A time=00:00:02.57 bitrate=N/A speed=1.02x elapsed=0:00:02.57    \r[Parsed_astats_0 @ 000001932de0cd80] Channel: 1\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -10.341803\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 2\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -10.341803\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 3\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -15.851121\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 4\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -15.849477\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 5\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -8.850507\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 6\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -50.308734\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 7\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -32.935845\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 8\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -32.947606\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 9\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -10.341803\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 10\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -10.341803\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 11\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -inf\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 12\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -inf\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 13\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -inf\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 14\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -inf\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 15\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -inf\n" +
        "[Parsed_astats_0 @ 000001932de0cd80] Channel: 16\n[Parsed_astats_0 @ 000001932de0cd80] Peak level dB: -inf\n" +
        "size=N/A time=00:00:03.00 bitrate=N/A speed=   1x elapsed=0:00:03.00\n";

    [Fact]
    public void ParseAstats_RealDuo2SixteenChannels_KeepsChannelOneDespiteTheProgressLine()
    {
        var peaks = FfmpegDeviceEnumerator.ParseAstatsPeaks(Duo2Sixteen);

        Assert.Equal(16, peaks.Count);
        Assert.Equal(-10.34, peaks[0], 2);   // canal 1 (pegado a la línea de progreso) no se pierde ni se desplaza
        Assert.Equal(-15.85, peaks[2], 2);
        Assert.Equal(-50.31, peaks[5], 2);   // canal 6 casi mudo
        Assert.Equal(-10.34, peaks[8], 2);   // 9-10 repite el nivel de 1-2
        Assert.All(peaks.Skip(10), p => Assert.Equal(double.NegativeInfinity, p));

        var probe = new AudioProbe(16, peaks);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, probe.ActivePairs); // 11-16 en silencio no cuentan
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
