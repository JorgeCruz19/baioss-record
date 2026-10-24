using Baioss.Record.Engine.FFmpeg.Clips;
using Baioss.Record.Infrastructure.Review;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Por dónde suena «Revisar» (nunca por una DeckLink: lo que suena por ella sale por la SDI de la tarjeta) y adónde salta su vídeo
/// cuando la CPU no da abasto (al primer fotograma clave por delante del audio).
/// </summary>
public sealed class ReviewAudioRouteTests
{
    private static AudioOutputInfo Out(int id, string name, string? adapter = null, bool isDefault = false) => new(id, name, adapter, isDefault);

    [Fact]
    public void TheDefaultOutput_IsUsed_WhenItIsNotADeckLink()
    {
        var outputs = new[] { Out(0, "Altavoces (Blackmagic DeckLink Duo 2 (1) Audio)"), Out(1, "Altavoces (Realtek(R) Audio)", isDefault: true) };
        Assert.Equal(1, ReviewAudioRoute.Pick(outputs)!.Id);
    }

    [Fact]
    public void ADeckLinkDefault_IsSkipped_ForTheFirstOtherOutput()
    {
        // El equipo de pruebas el 2026-10-23: la salida por defecto de Windows era la DeckLink (4).
        var outputs = new[]
        {
            Out(0, "Altavoces (Blackmagic DeckLink Duo 2 (1) Audio)", "Blackmagic DeckLink Duo 2 (1) Audio"),
            Out(1, "Altavoces (Blackmagic DeckLink Duo 2 (4) Audio)", "Blackmagic DeckLink Duo 2 (4) Audio", isDefault: true),
            Out(2, "Altavoces (NoMachine Microphone Adapter)", "NoMachine Microphone Adapter"),
        };
        var chosen = ReviewAudioRoute.Pick(outputs);
        Assert.Equal(2, chosen!.Id);
        Assert.False(chosen.IsDefault);
    }

    [Fact]
    public void ARenamedDeckLinkOutput_IsStillADeckLink_ByItsAdapter()
    {
        var renamed = Out(0, "Salida SDI 4", "Blackmagic DeckLink Duo 2 (4) Audio", isDefault: true);
        Assert.True(renamed.IsDeckLink);
        Assert.Null(ReviewAudioRoute.Pick(new[] { renamed }));
        // Sin la API de dispositivos queda el nombre de waveOut, cortado en 31 letras: «Blackmagic» sigue dentro.
        Assert.True(Out(1, "Altavoces (Blackmagic DeckLink D").IsDeckLink);
        Assert.True(Out(2, "Speakers (Blackmagic UltraStudio").IsDeckLink);
        Assert.False(Out(3, "Speakers (High Definition Audio ").IsDeckLink);
    }

    [Fact]
    public void OnlyDeckLinks_OrNoOutputAtAll_MeansNoSound()
    {
        Assert.Null(ReviewAudioRoute.Pick(new[] { Out(0, "Altavoces (Blackmagic DeckLink Duo 2 (1) Audio)", isDefault: true),
                                                  Out(1, "Altavoces (Blackmagic DeckLink Duo 2 (2) Audio)") }));
        Assert.Null(ReviewAudioRoute.Pick(Array.Empty<AudioOutputInfo>()));
    }

    [Fact]
    public void TheVideoJumpsTo_TheFirstKeyframeAtOrAfterTheAudio()
    {
        var packets = new[] { (10.0, 9.96, true), (10.04, 10.0, false), (12.0, 11.96, true), (12.04, 12.0, false), (14.0, 13.96, true) };
        Assert.Equal(12.0, FfprobeMedia.FirstKeyframeAtOrAfter(packets, 10.5));
        Assert.Equal(10.0, FfprobeMedia.FirstKeyframeAtOrAfter(packets, 10.0005)); // 1 ms de margen
        Assert.Null(FfprobeMedia.FirstKeyframeAtOrAfter(packets, 14.5));           // ninguna en el tramo buscado
    }
}
