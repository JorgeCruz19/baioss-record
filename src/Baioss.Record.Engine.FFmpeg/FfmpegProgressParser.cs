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
        long outTimeUs = GetLong("out_time_us");

        // Heurística simple de salud de buffer: caída de fps respecto a la nominal.
        long delta = frame - _lastFrame;
        _lastFrame = frame;
        double bufferHealth = delta > 0 ? 1.0 : 0.5;

        var tc = Timecode.FromFrameNumber(frame, nominalRate: 25);
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
