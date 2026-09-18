using System.Globalization;
using Baioss.Record.Application.Localization;

namespace Baioss.Record.Application.Capture;

/// <summary>
/// Qué audio se pide al dispositivo y qué par(es) de canales se graban. Es configuración de la ENTRADA (viaja en
/// <c>InputSource.Parameters</c>), porque describe la señal de la instalación —qué lleva cada par del SDI—, no el
/// formato de salida, que es cosa del preset:
/// <list type="bullet">
///   <item><c>audio_channels</c>: «2», «8», «16» o «auto» (pedir 16 y bajar a 8 y a 2 si la tarjeta no puede).</item>
///   <item><c>audio_pairs</c>: pares 1-based separados por comas («1» = canales 1-2, «2» = 3-4…) o «all».</item>
/// </list>
/// Sin parámetros se comporta como siempre: 2 canales, par 1. La tarjeta NO dice cuántos canales trae la señal
/// (se le pide un número y entrega ese número, con silencio en los que no existen), así que la decisión se toma
/// al configurar la entrada y queda FIJA mientras se graba: un par callado al empezar no es un par ausente, y las
/// pistas de un archivo no pueden cambiar a mitad.
/// </summary>
public sealed record AudioSelection(int RequestedChannels, bool Auto, IReadOnlyList<int> Pairs, bool AllPairs)
{
    public const string ChannelsKey = "audio_channels";
    public const string PairsKey = "audio_pairs";
    public const string AutoValue = "auto";
    public const string AllValue = "all";
    public const int MaxChannels = 16;

    /// <summary>Recuentos que admite el demuxer decklink de FFmpeg (opción <c>-channels</c>).</summary>
    public static readonly IReadOnlyList<int> ValidChannelCounts = new[] { 2, 8, 16 };

    /// <summary>Comportamiento de siempre: 2 canales, par 1.</summary>
    public static readonly AudioSelection Default = new(2, false, new[] { 1 }, false);

    /// <summary>Nº de canales con el que se ABRE el dispositivo la primera vez (en «auto», el máximo).</summary>
    public int InitialChannels => Auto ? MaxChannels : RequestedChannels;

    public static AudioSelection FromParameters(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null) return Default;

        bool auto = false;
        int channels = 2;
        if (parameters.TryGetValue(ChannelsKey, out var ch) && !string.IsNullOrWhiteSpace(ch))
        {
            var v = ch.Trim();
            if (v.Equals(AutoValue, StringComparison.OrdinalIgnoreCase)) auto = true;
            else if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && ValidChannelCounts.Contains(n)) channels = n;
        }

        var pairs = new List<int>();
        bool all = false;
        if (parameters.TryGetValue(PairsKey, out var ps) && !string.IsNullOrWhiteSpace(ps))
        {
            if (ps.Trim().Equals(AllValue, StringComparison.OrdinalIgnoreCase)) all = true;
            else
            {
                foreach (var tok in ps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p >= 1 && p <= MaxChannels / 2 && !pairs.Contains(p))
                        pairs.Add(p);
            }
        }
        if (!all && pairs.Count == 0) pairs.Add(1);
        return new AudioSelection(auto ? MaxChannels : channels, auto, pairs, all);
    }

    /// <summary>Escribe la selección en los parámetros de una entrada (para el gestor de entradas).</summary>
    public void WriteTo(IDictionary<string, string> parameters)
    {
        parameters[ChannelsKey] = Auto ? AutoValue : RequestedChannels.ToString(CultureInfo.InvariantCulture);
        parameters[PairsKey] = AllPairs ? AllValue : string.Join(',', Pairs.Select(p => p.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Canales (0-based) que se graban, en orden, acotados a los <paramref name="availableChannels"/> que entrega la
    /// fuente de verdad. Un par fuera de rango (p. ej. el 3-4 en una fuente de 2 canales) cae al par 1: mejor grabar
    /// el par que existe que no grabar nada. Nunca devuelve vacío mientras haya al menos un canal.
    /// </summary>
    public IReadOnlyList<int> ChannelIndexes(int availableChannels)
    {
        int n = Math.Max(1, availableChannels);
        if (AllPairs) return Enumerable.Range(0, n).ToArray();
        var list = new List<int>();
        foreach (var p in Pairs)
        {
            int a = (p - 1) * 2;
            if (a < n) list.Add(a);
            if (a + 1 < n) list.Add(a + 1);
        }
        if (list.Count == 0) { list.Add(0); if (n > 1) list.Add(1); }
        return list;
    }

    /// <summary>
    /// Pares (1-based) que se graban de verdad con <paramref name="availableChannels"/> canales: los elegidos que existen
    /// (o el 1 si ninguno existe, como en <see cref="ChannelIndexes"/>), o todos con «all». Para marcar en los medidores
    /// qué pares van al archivo y cuáles solo se miden.
    /// </summary>
    public IReadOnlyList<int> SelectedPairs(int availableChannels)
    {
        int pairs = Math.Max(1, availableChannels / 2);
        if (AllPairs) return Enumerable.Range(1, pairs).ToArray();
        var list = Pairs.Where(p => p <= pairs).ToList();
        if (list.Count == 0) list.Add(1);
        return list;
    }

    /// <summary>
    /// True si hay que ELEGIR canales con un filtro <c>pan</c>: la fuente entrega más de un estéreo. Con dos canales
    /// no hay nada que elegir y se deja la tubería de siempre (<c>-ac</c>), que mezclaría solo si hiciera falta.
    /// </summary>
    public static bool RequiresRouting(int availableChannels) => availableChannels > 2;

    /// <summary>Siguiente escalón hacia abajo que admite FFmpeg (16→8→2), o 0 si ya no hay.</summary>
    public static int NextLower(int channels) => channels switch { 16 => 8, 8 => 2, _ => 0 };

    /// <summary>Etiqueta corta de un par: 1 → «1-2», 2 → «3-4»…</summary>
    public static string PairLabel(int pair) => string.Create(CultureInfo.InvariantCulture, $"{pair * 2 - 1}-{pair * 2}");

    /// <summary>Lo seleccionado en palabras, en el idioma de la aplicación: «Par 3-4 de 8», «Pares 1-2 y 3-4 de 8», «Los 16 canales».</summary>
    public string Describe(int availableChannels)
    {
        if (AllPairs) return Localizer.F("Audio_AllChannels", availableChannels);
        var labels = Pairs.Select(PairLabel).ToList();
        if (labels.Count == 1) return Localizer.F("Audio_PairOf", labels[0], availableChannels);
        string joined = string.Join(", ", labels.Take(labels.Count - 1)) + " " + Localizer.T("Common_And") + " " + labels[^1];
        return Localizer.F("Audio_PairsOf", joined, availableChannels);
    }
}
