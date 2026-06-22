using System.Globalization;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Recording;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Acumula las líneas key=value que emite FFmpeg con <c>-progress pipe:1</c> y, en
/// cada bloque "progress=continue/end", produce una muestra <see cref="RecorderStats"/>.
/// </summary>
public sealed class FfmpegProgressParser
{
    private readonly Dictionary<string, string> _kv = new(StringComparer.Ordinal);
    private long _lastFrame;

    /// <summary>
    /// Tasa nominal (entera: 24/25/30/50/60) de la fuente/salida. Solo determina la subdivisión de
    /// cuadros (FF) del timecode; el HH:MM:SS viene del tiempo real de FFmpeg. Ajustar antes de grabar
    /// según la señal real evita que el contador corra al doble con fuentes a 50/60 fps.
    /// </summary>
    public int NominalRate { get; set; } = 25;

    /// <summary>Procesa una línea; devuelve stats cuando se cierra un bloque de progreso.</summary>
    public RecorderStats? Feed(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;

        string key = line[..eq].Trim();
        string value = line[(eq + 1)..].Trim();
        _kv[key] = value;

        if (key != "progress") return null; // bloque aún incompleto

        var stats = Snapshot();
        _kv.Clear();
        return stats;
    }

    private RecorderStats Snapshot()
    {
        double fps = GetDouble("fps");
        long frame = GetLong("frame");
        long drop = GetLong("drop_frames");
        long dup = GetLong("dup_frames");
        long bitrate = (long)(GetLeadingDouble("bitrate") * 1000); // "2046.7kbits/s" → bps
        // Tiempo real transcurrido de la salida. 'out_time_us' es el campo moderno; algunos builds solo
        // emiten 'out_time_ms' (que, por un histórico de FFmpeg, también está en microsegundos).
        long outTimeUs = GetLong("out_time_us");
        if (outTimeUs == 0) outTimeUs = GetLong("out_time_ms");

        // Heurística simple de salud de buffer: caída de fps respecto a la nominal.
        long delta = frame - _lastFrame;
        _lastFrame = frame;
        double bufferHealth = delta > 0 ? 1.0 : 0.5;

        // El timecode se deriva del TIEMPO real (out_time), no de 'frame ÷ tasa fija': así el contador
        // avanza 1 s por segundo sea cual sea la tasa de la fuente (antes, a 50 fps con divisor 25 fijo,
        // subía de dos en dos). La tasa solo fija la subdivisión de cuadros (FF).
        var tc = Timecode.FromMicroseconds(outTimeUs, NominalRate);
        return new RecorderStats(
            InputFps: fps,
            OutputFps: fps,
            DroppedFrames: drop,
            DuplicatedFrames: dup,
            Bitrate: new Bitrate(bitrate),
            BufferHealth: bufferHealth,
            Timecode: tc,
            FrameCount: frame);
    }

    private double GetDouble(string key) =>
        _kv.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>Parsea el prefijo numérico de valores con unidad, p. ej. "2046.7kbits/s".</summary>
    private double GetLeadingDouble(string key)
    {
        if (!_kv.TryGetValue(key, out var v)) return 0;
        int i = 0;
        while (i < v.Length && (char.IsDigit(v[i]) || v[i] is '.' or '-' or '+')) i++;
        return double.TryParse(v[..i], NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private long GetLong(string key) =>
        _kv.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0;
}
