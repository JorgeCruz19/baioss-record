using System.Globalization;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Interpreta las líneas de nivel que el filtro <c>ebur128=peak=true</c> escribe por <em>stderr</em>:
/// «… FTPK: -16.6 -16.9 dBFS …» es el true-peak del último bloque, UN VALOR POR CANAL del flujo medido
/// (1 mono, 2 estéreo; el orden es el de los canales de la fuente). Con audio multicanal cada canal lleva su propio ebur128
/// mono con nombre (<see cref="ChannelMeter"/>) y una línea suya: «[ebur128@ch3of16 @ …] … FTPK:  -9.0 dBFS …»
/// (<see cref="ParseChannelPeak"/>; las junta <see cref="FfmpegMeterReader"/>).
/// El silencio digital sale como «-inf», que .NET no parsea: aquí es el suelo de los medidores. Puro y testeable.
/// </summary>
public static class FfmpegMeterParser
{
    /// <summary>Suelo de los medidores (dBFS): silencio digital o valor ilegible.</summary>
    public const double FloorDb = -60;

    /// <summary>Principio del nombre de la instancia de ebur128 que mide UN canal («ebur128@ch3of16»: el canal 3 de 16).</summary>
    public const string ChannelMeterPrefix = "ebur128@ch";

    /// <summary>
    /// El ebur128 que mide el canal <paramref name="channel"/> (1-based) de los <paramref name="count"/> medidos, para un flujo MONO
    /// con ese canal. FFmpeg pone el nombre de la instancia al principio de cada línea del filtro: así se sabe de qué canal es.
    /// </summary>
    public static string ChannelMeter(int channel, int count)
        => string.Create(CultureInfo.InvariantCulture, $"{ChannelMeterPrefix}{channel}of{count}=peak=true");

    /// <summary>
    /// El true-peak de una línea de un medidor de UN canal (<see cref="ChannelMeter"/>): el canal (1-based), cuántos se miden y su
    /// valor; o <c>null</c> si la línea no es de nivel (también el «Summary:» que el filtro escribe al terminar).
    /// </summary>
    public static (int Channel, int Count, double Peak)? ParseChannelPeak(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith("[" + ChannelMeterPrefix, StringComparison.Ordinal)) return null;
        int start = ChannelMeterPrefix.Length + 1;
        int end = line.IndexOf(" @ ", start, StringComparison.Ordinal);
        int of = end < 0 ? -1 : line.IndexOf("of", start, end - start, StringComparison.Ordinal);
        if (of < 0
            || !int.TryParse(line.AsSpan(start, of - start), NumberStyles.None, CultureInfo.InvariantCulture, out int channel)
            || !int.TryParse(line.AsSpan(of + 2, end - of - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || channel < 1 || channel > count)
            return null;
        return ParseTruePeaks(line) is [var peak] ? (channel, count, peak) : null;
    }

    /// <summary>True-peak (dBFS) de cada canal, o <c>null</c> si la línea no es de nivel o no trae ningún valor.</summary>
    public static double[]? ParseTruePeaks(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int ftpk = line.IndexOf("FTPK:", StringComparison.Ordinal);
        if (ftpk < 0) return null;

        var values = new List<double>(2);
        foreach (var tok in line[(ftpk + 5)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.StartsWith("dBFS", StringComparison.Ordinal)) break;      // fin de la lista de canales
            if (tok.Equals("-inf", StringComparison.OrdinalIgnoreCase)) { values.Add(FloorDb); continue; }
            if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) break;
            // Un NaN/∞ rompería la serialización JSON del estado (la API) y los medidores: al suelo.
            values.Add(double.IsFinite(v) ? v : FloorDb);
        }
        return values.Count == 0 ? null : values.ToArray();
    }
}
