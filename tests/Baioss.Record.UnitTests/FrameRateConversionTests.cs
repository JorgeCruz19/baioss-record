using System.Globalization;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.ValueObjects;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// La conversión de cadencia que pide un preset sobre una fuente: cuándo hay conversión, qué fracción de frames descarta o
/// repite FFmpeg (lo que la alarma de frames perdidos descuenta) y cómo se le cuenta al operador en los dos idiomas.
/// </summary>
[Collection("Localizer")] // el idioma es estado global
public sealed class FrameRateConversionTests : IDisposable
{
    private readonly AppLanguage _language = Localizer.Language;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public FrameRateConversionTests()
    {
        Localizer.Language = AppLanguage.Spanish;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES"); // decimales con coma, como en la petición: «59,94»
    }

    public void Dispose()
    {
        Localizer.Language = _language;
        CultureInfo.CurrentCulture = _culture;
    }

    [Fact]
    public void NoOutputRate_IsNoConversion()
    {
        var c = FrameRateConversion.For(FrameRate.P5994, output: null);
        Assert.False(c.IsMismatch);
        Assert.False(c.IsUnknown);
        Assert.Equal(0, c.DroppedFraction);
        Assert.Null(c.Describe());
        Assert.Null(c.Badge());
    }

    [Fact]
    public void SameRate_IsNoConversion_EvenWrittenDifferently()
    {
        // 30000/1001 (preset) y «29.97» tal cual lo da un volcado de FFmpeg (2997/100) son la misma señal.
        Assert.False(FrameRateConversion.For(new FrameRate(2997, 100), FrameRate.P2997).IsMismatch);
        Assert.False(FrameRateConversion.For(FrameRate.P25, new FrameRate(50, 2)).IsMismatch);
    }

    [Fact]
    public void ThirtyAndTwentyNineNinetySeven_AreAConversion()
    {
        // 30 frente a 29,97 es un 0,1 %, pero FFmpeg tira de verdad un frame de cada 1001.
        var c = FrameRateConversion.For(FrameRate.P30, FrameRate.P2997);
        Assert.True(c.DropsFrames);
        Assert.Equal(1.0 / 1001, c.DroppedFraction, 6);
    }

    [Theory]
    [InlineData(30, 1, 25, 1, 1.0 / 6)]               // 30 → 25: uno de cada seis frames de entrada
    [InlineData(60000, 1001, 30000, 1001, 0.5)]       // 59,94 → 29,97: la mitad
    [InlineData(30000, 1001, 25, 1, 0.16583)]         // 29,97 → 25
    [InlineData(60000, 1001, 25, 1, 0.58292)]         // 59,94 → 25
    [InlineData(50, 1, 25, 1, 0.5)]
    public void DownConversion_DropsTheExpectedShareOfInputFrames(int sn, int sd, int on, int od, double expected)
    {
        var c = FrameRateConversion.For(new FrameRate(sn, sd), new FrameRate(on, od));
        Assert.True(c.DropsFrames);
        Assert.False(c.DuplicatesFrames);
        Assert.Equal(expected, c.DroppedFraction, 4);
        Assert.Equal(0, c.DuplicatedFraction);
    }

    [Fact]
    public void UpConversion_RepeatsFrames_AndDropsNone()
    {
        // El caso de producción: una señal 1080i59,94 (29,97 frames) grabada con un preset progresivo a 59,94.
        var c = FrameRateConversion.For(FrameRate.P2997, FrameRate.P5994);
        Assert.True(c.DuplicatesFrames);
        Assert.False(c.DropsFrames);
        Assert.Equal(0, c.DroppedFraction);
        Assert.Equal(0.5, c.DuplicatedFraction, 6);
    }

    [Fact]
    public void FixedOutput_WithUnknownSource_IsUnknown()
    {
        var c = FrameRateConversion.For(source: null, FrameRate.P25);
        Assert.True(c.IsUnknown);
        Assert.False(c.IsMismatch);
        Assert.Equal(0, c.DroppedFraction);
        Assert.Null(c.Describe());
    }

    [Fact]
    public void InvalidRates_CountAsUnknown()
    {
        Assert.False(FrameRateConversion.For(new FrameRate(0, 1), FrameRate.P25).IsMismatch);
        Assert.True(FrameRateConversion.For(new FrameRate(0, 1), FrameRate.P25).IsUnknown);
        Assert.False(FrameRateConversion.For(FrameRate.P25, new FrameRate(25, 0)).IsMismatch);
    }

    [Fact]
    public void Describe_Spanish_HalfTheFrames()
    {
        var text = FrameRateConversion.For(FrameRate.P5994, FrameRate.P2997).Describe();
        Assert.Equal("La fuente llega a 59,94 fps y el preset graba a 29,97 fps: FFmpeg descartará la mitad de los frames para convertir la cadencia. Es la conversión que pide el preset, no un fallo del equipo.", text);
        Assert.Equal("59,94 → 29,97 fps", FrameRateConversion.For(FrameRate.P5994, FrameRate.P2997).Badge());
    }

    [Theory]
    [InlineData(30, 1, 25, 1, "1 de cada 6 frames")]
    [InlineData(30000, 1001, 25, 1, "1 de cada 6 frames")]     // 6,03: casi entero
    [InlineData(60000, 1001, 25, 1, "el 58 % de los frames")]  // 1,72: porcentaje
    [InlineData(30, 1, 30000, 1001, "1 de cada 1001 frames")]
    public void Describe_Spanish_ShareOfDroppedFrames(int sn, int sd, int on, int od, string share)
        => Assert.Contains($"FFmpeg descartará {share} para convertir", FrameRateConversion.For(new FrameRate(sn, sd), new FrameRate(on, od)).Describe());

    [Fact]
    public void Describe_Spanish_Duplicates()
    {
        Assert.Equal("La fuente llega a 29,97 fps y el preset graba a 59,94 fps: FFmpeg repetirá frames para rellenar la cadencia (copias: la mitad de los frames grabados).",
            FrameRateConversion.For(FrameRate.P2997, FrameRate.P5994).Describe());
        Assert.Contains("(copias: 1 de cada 6 frames grabados)", FrameRateConversion.For(FrameRate.P25, FrameRate.P30).Describe());
    }

    [Fact]
    public void Describe_English()
    {
        Localizer.Language = AppLanguage.English;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        Assert.Equal("The source arrives at 59.94 fps and the preset records at 29.97 fps: FFmpeg will drop half of the frames to convert the frame rate. This is the conversion the preset asks for, not a machine problem.",
            FrameRateConversion.For(FrameRate.P5994, FrameRate.P2997).Describe());
        Assert.Contains("will drop 1 in every 6 frames", FrameRateConversion.For(FrameRate.P30, FrameRate.P25).Describe());
        Assert.Contains("will drop 58% of the frames", FrameRateConversion.For(FrameRate.P5994, FrameRate.P25).Describe());
        Assert.Contains("(copies: 1 in every 6 recorded frames)", FrameRateConversion.For(FrameRate.P25, FrameRate.P30).Describe());
        Assert.Equal("59.94 → 29.97 fps", FrameRateConversion.For(FrameRate.P5994, FrameRate.P2997).Badge());
    }

    [Fact]
    public void Fps_FollowsTheMachineCulture()
    {
        Assert.Equal("25", FrameRateConversion.Fps(FrameRate.P25));
        Assert.Equal("29,97", FrameRateConversion.Fps(FrameRate.P2997));
        CultureInfo.CurrentCulture = new CultureInfo("es-HN"); // Honduras: punto decimal
        Assert.Equal("29.97", FrameRateConversion.Fps(FrameRate.P2997));
    }

    [Theory]
    // Un cuadro por campo: 1080i29.97 → 1080p59.94 (bwdif) no repite nada.
    [InlineData(30000, 1001, ScanType.InterlacedTff, 60000, 1001, ScanType.Progressive)]
    [InlineData(25, 1, ScanType.InterlacedBff, 50, 1, ScanType.Progressive)]
    // Dos cuadros tejidos en uno: 1080p59.94 → 1080i29.97 (interlace) no descarta nada.
    [InlineData(60000, 1001, ScanType.Progressive, 30000, 1001, ScanType.InterlacedTff)]
    [InlineData(50, 1, ScanType.Progressive, 25, 1, ScanType.InterlacedBff)]
    public void FieldsToFramesOrFramesToFields_IsNoConversion(int sn, int sd, ScanType sourceScan, int on, int od, ScanType outputScan)
    {
        var c = FrameRateConversion.For(new FrameRate(sn, sd), new FrameRate(on, od), sourceScan, outputScan);
        Assert.True(c.ByFields);
        Assert.False(c.IsMismatch);
        Assert.Equal(0, c.DroppedFraction);
        Assert.Equal(0, c.DuplicatedFraction);
        Assert.Null(c.Badge());
    }

    [Theory]
    [InlineData(30000, 1001, ScanType.Progressive, 60000, 1001, ScanType.Progressive)]   // un progresivo al doble sí repite
    [InlineData(30000, 1001, null, 60000, 1001, ScanType.Progressive)]                    // barrido desconocido: como siempre
    [InlineData(60000, 1001, ScanType.Progressive, 25, 1, ScanType.InterlacedTff)]        // 59,94 → 25i no es tejer: descarta
    [InlineData(60000, 1001, ScanType.InterlacedTff, 30000, 1001, ScanType.InterlacedTff)]
    public void OtherwiseTheScanChangesNothing(int sn, int sd, ScanType? sourceScan, int on, int od, ScanType outputScan)
    {
        var c = FrameRateConversion.For(new FrameRate(sn, sd), new FrameRate(on, od), sourceScan, outputScan);
        Assert.False(c.ByFields);
        Assert.True(c.IsMismatch);
    }
}
