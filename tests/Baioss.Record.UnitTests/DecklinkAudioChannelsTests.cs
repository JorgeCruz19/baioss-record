using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Canales de audio que se piden a la DeckLink y retroceso cuando la tarjeta no los admite.</summary>
public class DecklinkAudioChannelsTests
{
    private static DecklinkCaptureSource Source(string? channels = null, string? pairs = null)
    {
        var def = new InputSource { Name = "DeckLink Duo (1)", Type = InputType.DecklinkSdi, Uri = "DeckLink Duo (1)" };
        if (channels is not null) def.Parameters[AudioSelection.ChannelsKey] = channels;
        if (pairs is not null) def.Parameters[AudioSelection.PairsKey] = pairs;
        return new DecklinkCaptureSource(def);
    }

    private static string Args(DecklinkCaptureSource s) => string.Join(' ', s.BuildInputArguments());

    [Fact]
    public void SinParametros_PideDosCanales_ComoSiempre()
    {
        var s = Source();

        Assert.Equal(2, s.AudioChannelCount);
        Assert.Contains("-channels 2", Args(s));
        Assert.False(s.TryReduceAudioChannels());
    }

    [Fact]
    public void Auto_PideDieciseis_YBajaA8YA2_SiLaTarjetaNoPuede()
    {
        var s = Source(channels: "auto");
        Assert.Equal(16, s.AudioChannelCount);
        Assert.Contains("-channels 16", Args(s));

        Assert.True(s.TryReduceAudioChannels());
        Assert.Equal(8, s.AudioChannelCount);
        Assert.Contains("-channels 8", Args(s));

        Assert.True(s.TryReduceAudioChannels());
        Assert.Equal(2, s.AudioChannelCount);

        Assert.False(s.TryReduceAudioChannels()); // ya no hay más escalones
        Assert.Equal(2, s.AudioChannelCount);
    }

    [Fact]
    public void RecuentoFijo_NoBaja()
    {
        var s = Source(channels: "8");

        Assert.False(s.TryReduceAudioChannels());
        Assert.Equal(8, s.AudioChannelCount);
        Assert.Contains("-channels 8", Args(s));
    }

    [Fact]
    public void LaOpcionDeCanales_VaAntesDeLaEntrada()
    {
        // Las opciones del demuxer decklink deben preceder a su -i.
        var args = Source(channels: "16").BuildInputArguments();
        Assert.True(args.ToList().IndexOf("-channels") < args.ToList().IndexOf("-i"));
    }

    [Fact]
    public async Task AlAbrir_PublicaCanalesYSeleccionEnPalabras()
    {
        var previous = Localizer.Language;
        Localizer.Language = AppLanguage.Spanish;
        try
        {
            var s = Source(channels: "8", pairs: "2");
            await s.OpenAsync();

            Assert.Equal(8, s.CurrentSignal.AudioChannels);
            Assert.Equal("Par 3-4 de 8", s.CurrentSignal.AudioSelectionLabel);

            var plain = Source();
            await plain.OpenAsync();
            Assert.Equal(2, plain.CurrentSignal.AudioChannels);
            Assert.Null(plain.CurrentSignal.AudioSelectionLabel); // con 2 canales no hay nada que elegir
        }
        finally { Localizer.Language = previous; }
    }
}
