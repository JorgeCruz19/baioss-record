namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Junta las líneas de los medidores de los procesos de UN canal en un true-peak por canal de audio:
/// <list type="bullet">
///   <item>un ebur128 con todos los canales (mono, estéreo) da la lista entera en una línea;</item>
///   <item>con audio multicanal cada canal tiene su ebur128 (ver <see cref="FfmpegMeterParser.ChannelMeter"/>) y su línea. La del
///   último canal cierra la ronda: los medidores de un proceso escriben en el mismo bloque de 100 ms y FFmpeg los atiende en el orden
///   del grafo (medido con FFmpeg 8.0 y con el de junio de 2026: 600 rondas de 16 de 600 en orden). Un canal que faltase en una ronda
///   se queda con su último valor.</item>
/// </list>
/// Durante un relevo escriben dos procesos de la misma fuente a la vez, cada uno desde su hilo: sus valores se mezclan, como ya
/// pasaba con las líneas enteras. Si cambia el número de canales (la tarjeta no admitió 16 y se pasó a 8), se empieza de cero.
/// </summary>
public sealed class FfmpegMeterReader
{
    private readonly object _lock = new();
    private double[] _peaks = Array.Empty<double>();

    /// <summary>
    /// <c>true</c> si la línea es de un medidor (no hace falta buscarle otro sentido). <paramref name="peaks"/> trae un true-peak por
    /// canal cuando con esta línea hay unos niveles nuevos que publicar; si no, <c>null</c>.
    /// </summary>
    public bool TryRead(string line, out double[]? peaks)
    {
        peaks = null;
        if (string.IsNullOrEmpty(line)) return false;
        if (line.StartsWith("[" + FfmpegMeterParser.ChannelMeterPrefix, StringComparison.Ordinal))
        {
            if (FfmpegMeterParser.ParseChannelPeak(line) is not { } level) return false; // p. ej. su «Summary:» al terminar
            lock (_lock)
            {
                if (_peaks.Length != level.Count)
                {
                    _peaks = new double[level.Count];
                    Array.Fill(_peaks, FfmpegMeterParser.FloorDb);
                }
                _peaks[level.Channel - 1] = level.Peak;
                if (level.Channel == level.Count) peaks = (double[])_peaks.Clone();
            }
            return true;
        }
        peaks = FfmpegMeterParser.ParseTruePeaks(line);
        return peaks is not null;
    }
}
