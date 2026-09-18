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
}
