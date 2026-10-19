using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Parseo de los niveles true-peak por canal que ebur128 escribe por stderr (medidores VU).</summary>
public class FfmpegMeterParserTests
{
    private const string Prefix = "[Parsed_ebur128_1 @ 000001] t: 1.4   TARGET:-23 LUFS    M: -14.4 S: -14.5     I: -14.5 LUFS       LRA:   0.0 LU  ";

    [Fact]
    public void Stereo_TwoValuesInOrder()
    {
        var peaks = FfmpegMeterParser.ParseTruePeaks(Prefix + "FTPK: -16.6 -16.9 dBFS  TPK: -8.0 -8.1 dBFS");

        Assert.NotNull(peaks);
        Assert.Equal(new[] { -16.6, -16.9 }, peaks);
    }

    [Fact]
    public void Mono_OneValue()
        => Assert.Equal(new[] { -3.5 }, FfmpegMeterParser.ParseTruePeaks(Prefix + "FTPK:  -3.5 dBFS  TPK:  -3.5 dBFS"));

    [Fact]
    public void Multichannel_OneValuePerCapturedChannel()
    {
        // Fuente de 8 canales medida entera: los 8 true-peak llegan en el orden de la tarjeta.
        var peaks = FfmpegMeterParser.ParseTruePeaks(Prefix + "FTPK:  -3.1  -9.1 -14.9 -20.9 -26.9 -33.2 -39.2 -45.0 dBFS  TPK: …");

        Assert.NotNull(peaks);
        Assert.Equal(8, peaks!.Length);
        Assert.Equal(-3.1, peaks[0], 3);
        Assert.Equal(-45.0, peaks[7], 3);
    }

    [Fact]
    public void DigitalSilence_IsTheMeterFloor_NotAParseFailure()
    {
        // ebur128 imprime «-inf» en silencio digital (slate, canales sin nada), que .NET no parsea: es el suelo.
        var peaks = FfmpegMeterParser.ParseTruePeaks(Prefix + "FTPK:  -inf  -inf -18.1  -inf dBFS  TPK:  -inf  -inf -18.1  -inf dBFS");

        Assert.Equal(new[] { FfmpegMeterParser.FloorDb, FfmpegMeterParser.FloorDb, -18.1, FfmpegMeterParser.FloorDb }, peaks);
    }

    [Fact]
    public void OtherLines_AreNotLevels()
    {
        Assert.Null(FfmpegMeterParser.ParseTruePeaks("frame=  120 fps= 25 q=-1.0 size=N/A time=00:00:04.80"));
        Assert.Null(FfmpegMeterParser.ParseTruePeaks("[silencedetect @ 0] silence_start: 3.2"));
        Assert.Null(FfmpegMeterParser.ParseTruePeaks(""));
        Assert.Null(FfmpegMeterParser.ParseTruePeaks("… FTPK: dBFS"));   // sin valores
    }

    // --- Un medidor por canal (audio multicanal): cada ebur128 mono lleva su nombre ---

    // Líneas reales de FFmpeg 8.0 y de la build de junio de 2026 (las dos escriben lo mismo).
    private const string Channel3Of16 = "[ebur128@ch3of16 @ 000001b9f191f840] t: 0.0999792  TARGET:-23 LUFS    M:-120.7 S:-120.7     I: -70.0 LUFS       LRA:   0.0 LU  FTPK:  -9.0 dBFS  TPK:  -9.0 dBFS";

    [Fact]
    public void ChannelMeter_NamesTheInstanceAfterItsChannelAndTheCount()
        => Assert.Equal("ebur128@ch3of16=peak=true", FfmpegMeterParser.ChannelMeter(3, 16));

    [Fact]
    public void ChannelPeak_SaysWhichChannelOfHowMany()
        => Assert.Equal((3, 16, -9.0), FfmpegMeterParser.ParseChannelPeak(Channel3Of16));

    [Fact]
    public void ChannelPeak_DigitalSilence_IsTheFloor()
        => Assert.Equal((12, 16, FfmpegMeterParser.FloorDb), FfmpegMeterParser.ParseChannelPeak(
            "[ebur128@ch12of16 @ 000002d494af8e80] t: 0.0999792  TARGET:-23 LUFS    M:-120.7 S:-120.7     I: -70.0 LUFS       LRA:   0.0 LU  FTPK:  -inf dBFS  TPK:  -inf dBFS"));

    [Theory]
    [InlineData("[ebur128@ch3of16 @ 000001b9f191f840] Summary:")]                    // lo que escribe al terminar
    [InlineData(Prefix + "FTPK: -16.6 -16.9 dBFS  TPK: -8.0 -8.1 dBFS")]               // un ebur128 con todos los canales
    [InlineData("[ebur128@chXof16 @ 0] t: 0.1  FTPK:  -9.0 dBFS")]
    [InlineData("[ebur128@ch17of16 @ 0] t: 0.1  FTPK:  -9.0 dBFS")]                     // un canal que no está entre los medidos
    [InlineData("[ebur128@ch0of16 @ 0] t: 0.1  FTPK:  -9.0 dBFS")]
    [InlineData("[ebur128@ch3of16] t: 0.1  FTPK:  -9.0 dBFS")]
    [InlineData("")]
    public void ChannelPeak_OnlyFromTheLevelLineOfAChannelMeter(string line)
        => Assert.Null(FfmpegMeterParser.ParseChannelPeak(line));
}

/// <summary>Los niveles de un canal: líneas con todos los canales, o una línea por canal que se juntan en una ronda.</summary>
public class FfmpegMeterReaderTests
{
    private static string Line(int channel, int count, string ftpk)
        => $"[ebur128@ch{channel}of{count} @ 00000213a0d5e100] t: 1.39998    TARGET:-23 LUFS    M:-120.7 S:-120.7     I: -70.0 LUFS       LRA:   0.0 LU  FTPK: {ftpk} dBFS  TPK: {ftpk} dBFS";

    [Fact]
    public void AllChannelsInOneLine_ArePublishedAtOnce()
    {
        var reader = new FfmpegMeterReader();

        Assert.True(reader.TryRead("[Parsed_ebur128_1 @ 01] t: 1.4   TARGET:-23 LUFS    M: -14.4 S: -14.5     I: -14.5 LUFS       LRA:   0.0 LU  FTPK: -16.6 -16.9 dBFS  TPK: -8.0 -8.1 dBFS", out var peaks));
        Assert.Equal(new[] { -16.6, -16.9 }, peaks);
    }

    [Fact]
    public void OneLinePerChannel_IsPublishedWithTheLastChannel_EachInItsPlace()
    {
        // Lo que dio FFmpeg 8.0 con 4 canales distintos: el canal 1 en silencio y los demás a −6, −9 y −12 dB.
        var reader = new FfmpegMeterReader();
        var levels = new[] { " -inf", " -6.0", " -9.0", "-12.0" };

        for (int c = 1; c <= 3; c++)
        {
            Assert.True(reader.TryRead(Line(c, 4, levels[c - 1]), out var partial));
            Assert.Null(partial);                                                  // la ronda no está completa
        }
        Assert.True(reader.TryRead(Line(4, 4, levels[3]), out var peaks));

        Assert.Equal(new[] { FfmpegMeterParser.FloorDb, -6.0, -9.0, -12.0 }, peaks);
    }

    [Fact]
    public void TheNextRound_ReplacesEachChannel_AndAChannelThatDoesNotReportKeepsItsLastValue()
    {
        var reader = new FfmpegMeterReader();
        foreach (var (c, v) in new[] { (1, "-3.0"), (2, "-6.0"), (3, "-9.0") }) reader.TryRead(Line(c, 3, v), out _);

        reader.TryRead(Line(1, 3, "-4.0"), out _);
        reader.TryRead(Line(3, 3, "-10.0"), out var peaks);                         // falta el canal 2

        Assert.Equal(new[] { -4.0, -6.0, -10.0 }, peaks);
    }

    [Fact]
    public void FewerChannels_StartAfresh()
    {
        // La tarjeta no admitió 16 canales y el canal vuelve con 8: nada del canal 9 en adelante.
        var reader = new FfmpegMeterReader();
        for (int c = 1; c <= 16; c++) reader.TryRead(Line(c, 16, "-20.0"), out _);

        reader.TryRead(Line(8, 8, "-7.0"), out var peaks);

        Assert.Equal(8, peaks!.Length);
        Assert.Equal(-7.0, peaks[7]);
        Assert.All(peaks[..7], p => Assert.Equal(FfmpegMeterParser.FloorDb, p));
    }

    [Fact]
    public void ThePublishedLevels_AreACopy()
    {
        var reader = new FfmpegMeterReader();
        reader.TryRead(Line(1, 2, "-3.0"), out _);
        reader.TryRead(Line(2, 2, "-6.0"), out var first);

        reader.TryRead(Line(1, 2, "-30.0"), out _);

        Assert.Equal(new[] { -3.0, -6.0 }, first);
    }

    [Theory]
    [InlineData("[ebur128@ch3of16 @ 000001b9f191f840] Summary:")]
    [InlineData("frame=  120 fps= 25 q=-1.0 size=N/A time=00:00:04.80")]
    [InlineData("[silencedetect @ 0] silence_start: 3.2")]
    [InlineData("")]
    public void OtherLines_AreNotMeterLevels(string line)
    {
        Assert.False(new FfmpegMeterReader().TryRead(line, out var peaks));
        Assert.Null(peaks);
    }
}
