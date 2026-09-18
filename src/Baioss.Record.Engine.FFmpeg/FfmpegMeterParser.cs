using System.Globalization;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Interpreta las líneas de nivel que el filtro <c>ebur128=peak=true</c> escribe por <em>stderr</em>:
/// «… FTPK: -16.6 -16.9 dBFS …» es el true-peak del último bloque, UN VALOR POR CANAL del flujo medido
/// (1 mono, 2 estéreo, 8 o 16 con audio embebido multicanal; el orden es el de los canales de la fuente).
/// El silencio digital sale como «-inf», que .NET no parsea: aquí es el suelo de los medidores. Puro y testeable.
/// </summary>
public static class FfmpegMeterParser
{
    /// <summary>Suelo de los medidores (dBFS): silencio digital o valor ilegible.</summary>
    public const double FloorDb = -60;

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
