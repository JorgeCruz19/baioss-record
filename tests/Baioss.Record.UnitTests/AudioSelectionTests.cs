using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Selección de audio de una entrada (canales pedidos y pares a grabar) leída de sus parámetros.</summary>
public class AudioSelectionTests
{
    private static AudioSelection Parse(string? channels = null, string? pairs = null)
    {
        var p = new Dictionary<string, string>();
        if (channels is not null) p[AudioSelection.ChannelsKey] = channels;
        if (pairs is not null) p[AudioSelection.PairsKey] = pairs;
        return AudioSelection.FromParameters(p);
    }

    [Theory]
    [InlineData(null, 8, new[] { 1 })]          // sin elegir: par 1
    [InlineData("1,3", 8, new[] { 1, 3 })]      // los elegidos que existen
    [InlineData("3,5", 8, new[] { 3 })]         // el 5 (canales 9-10) no existe con 8 canales
    [InlineData("5", 8, new[] { 1 })]           // ninguno existe → par 1, como ChannelIndexes
    [InlineData("all", 8, new[] { 1, 2, 3, 4 })]
    [InlineData("all", 16, new[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void SelectedPairs_SonLosParesQueDeVerdadSeGraban(string? pairs, int channels, int[] expected)
        => Assert.Equal(expected, Parse("8", pairs).SelectedPairs(channels));

    [Fact]
    public void SinParametros_EsElComportamientoDeSiempre()
    {
        var s = AudioSelection.FromParameters(null);

        Assert.Equal(2, s.RequestedChannels);
        Assert.False(s.Auto);
        Assert.Equal(new[] { 1 }, s.Pairs);
        Assert.False(s.AllPairs);
        Assert.Equal(new[] { 0, 1 }, s.ChannelIndexes(2));
        Assert.Equal(2, s.InitialChannels);
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("8", 8)]
    [InlineData("16", 16)]
    [InlineData("6", 2)]     // FFmpeg solo admite 2, 8 o 16 → un valor raro cae al de siempre
    [InlineData("abc", 2)]
    public void Canales_SoloAdmiteLosValoresDeFfmpeg(string value, int expected)
        => Assert.Equal(expected, Parse(channels: value).RequestedChannels);

    [Fact]
    public void Auto_EmpiezaPidiendoDieciseis()
    {
        var s = Parse(channels: "auto");

        Assert.True(s.Auto);
        Assert.Equal(16, s.InitialChannels);
    }

    [Fact]
    public void Pares_SeExpandenACanalesEnOrden()
    {
        Assert.Equal(new[] { 2, 3 }, Parse(pairs: "2").ChannelIndexes(8));               // par 2 = canales 3-4 (0-based 2,3)
        Assert.Equal(new[] { 0, 1, 2, 3 }, Parse(pairs: "1,2").ChannelIndexes(8));
        Assert.Equal(new[] { 4, 5, 0, 1 }, Parse(pairs: "3, 1").ChannelIndexes(8));      // el orden pedido se respeta
        Assert.Equal(Enumerable.Range(0, 8).ToArray(), Parse(pairs: "all").ChannelIndexes(8));
    }

    [Fact]
    public void ParFueraDeRango_CaeAlPrimerPar()
    {
        // Pedir el par 3-4 a una fuente de 2 canales: mejor grabar el par que existe que no grabar nada.
        Assert.Equal(new[] { 0, 1 }, Parse(pairs: "2").ChannelIndexes(2));
        Assert.Equal(new[] { 0, 1 }, Parse(pairs: "9").ChannelIndexes(2)); // par 9 no existe (máx. 8) → se ignora
    }

    [Fact]
    public void Enrutado_SoloConMasDeUnEstereo()
    {
        Assert.False(AudioSelection.RequiresRouting(2));
        Assert.True(AudioSelection.RequiresRouting(8));
        Assert.True(AudioSelection.RequiresRouting(16));
    }

    [Fact]
    public void Retroceso_VaDe16A8A2()
    {
        Assert.Equal(8, AudioSelection.NextLower(16));
        Assert.Equal(2, AudioSelection.NextLower(8));
        Assert.Equal(0, AudioSelection.NextLower(2));
    }

    [Fact]
    public void Escribir_YLeer_DevuelveLoMismo()
    {
        var original = new AudioSelection(8, false, new[] { 2, 3 }, false);
        var p = new Dictionary<string, string>();

        original.WriteTo(p);
        var back = AudioSelection.FromParameters(p);

        Assert.Equal("8", p[AudioSelection.ChannelsKey]);
        Assert.Equal("2,3", p[AudioSelection.PairsKey]);
        Assert.Equal(original.RequestedChannels, back.RequestedChannels);
        Assert.Equal(original.Pairs, back.Pairs);
    }

    [Fact]
    public void Descripcion_EnPalabras()
    {
        var previous = Localizer.Language;
        Localizer.Language = AppLanguage.Spanish;
        try
        {
            Assert.Equal("Par 3-4 de 8", Parse(channels: "8", pairs: "2").Describe(8));
            Assert.Equal("Pares 1-2 y 3-4 de 8", Parse(channels: "8", pairs: "1,2").Describe(8));
            Assert.Equal("Los 16 canales", Parse(channels: "16", pairs: "all").Describe(16));
        }
        finally { Localizer.Language = previous; }
    }
}
