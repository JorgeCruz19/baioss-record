namespace Baioss.Record.Infrastructure.Review;

/// <summary>Una salida de audio de Windows: su número de waveOut, su nombre, el de su adaptador (la tarjeta) y si es la de por
/// defecto.</summary>
public sealed record AudioOutputInfo(int Id, string Name, string? Adapter, bool IsDefault)
{
    /// <summary>
    /// Es una salida de una DeckLink (o de otra tarjeta Blackmagic): lo que suena por ella sale por la SDI/HDMI de la tarjeta,
    /// que puede ir a un equipo de la emisión. Se mira también el adaptador, que sigue diciendo «Blackmagic DeckLink…» aunque se
    /// haya cambiado el nombre de la salida en Windows.
    /// </summary>
    public bool IsDeckLink => Mentions(Name) || Mentions(Adapter);

    private static bool Mentions(string? text) => text is not null
        && (text.Contains("DeckLink", StringComparison.OrdinalIgnoreCase) || text.Contains("Blackmagic", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Por dónde suena la ventana «Revisar»: nunca por una DeckLink.</summary>
public static class ReviewAudioRoute
{
    /// <summary>La salida de Windows por defecto si no es de una DeckLink; si lo es, la primera que no lo sea; null si no queda
    /// ninguna (entonces se revisa sin sonido).</summary>
    public static AudioOutputInfo? Pick(IReadOnlyList<AudioOutputInfo> outputs)
        => outputs.FirstOrDefault(o => o.IsDefault && !o.IsDeckLink) ?? outputs.FirstOrDefault(o => !o.IsDeckLink);
}
