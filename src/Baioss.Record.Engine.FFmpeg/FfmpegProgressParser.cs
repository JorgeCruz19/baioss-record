using System.Diagnostics;
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
    private readonly Func<TimeSpan> _clock;
    // Bloques recientes del proceso actual (cuándo llegó cada uno y su contador de frames) para el ritmo instantáneo.
    private readonly List<(TimeSpan At, long Frame)> _recent = new();

    public FfmpegProgressParser() : this(null) { }

    /// <param name="clock">Reloj monotónico con el que se fecha cada bloque (los tests lo controlan); por defecto, el tiempo
    /// transcurrido desde que se creó el parser.</param>
    public FfmpegProgressParser(Func<TimeSpan>? clock)
    {
        if (clock is null)
        {
            var sw = Stopwatch.StartNew();
            clock = () => sw.Elapsed;
        }
        _clock = clock;
    }

    /// <summary>
    /// Tramo sobre el que se mide <see cref="RecorderStats.OutputFps"/>: los frames que FFmpeg contó entre el bloque más reciente
    /// y el último que llegó hace al menos esto, por el tiempo de pared entre ambos. El <c>fps</c> que imprime FFmpeg es la MEDIA
    /// desde que arrancó el proceso: tras vaciarse un atraso marcó 37,9–44 fps durante un minuto con una fuente de 29,97 (medido
    /// con una DeckLink). Unos segundos bastan para que el ±1 frame entre bloques no haga bailar la cifra.
    /// </summary>
    public TimeSpan RateWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Tasa nominal (entera: 24/25/30/50/60) de la fuente/salida. Solo determina la subdivisión de
    /// cuadros (FF) del timecode; el HH:MM:SS viene del tiempo real de FFmpeg. Ajustar antes de grabar
    /// según la señal real evita que el contador corra al doble con fuentes a 50/60 fps.
    /// </summary>
    public int NominalRate { get; set; } = 25;

    /// <summary>
    /// Frames que FFmpeg cuenta por cada frame de la FUENTE (<see cref="FfmpegArgumentBuilder.PreviewFramesPerSourceFrame"/>).
    /// <c>frame</c> y <c>fps</c> son los de la primera salida de vídeo, que en el proceso del canal es el preview: con una señal
    /// entrelazada saca un cuadro por campo (2) y, sin dividir, el FPS OUT, el contador de frames y la alarma de frames
    /// perdidos (que descuenta la conversión de cadencia por frame de ENTRADA) contarían el doble. <c>drop_frames</c> y
    /// <c>dup_frames</c> no se tocan: el preview, en passthrough, no descarta ni duplica nada.
    /// </summary>
    public int FramesPerSourceFrame { get; set; } = 1;

    /// <summary>
    /// Cadencia (cuadros por segundo) a la que contar el progreso por TIEMPO DE MEDIOS, o null para contar los frames del preview
    /// (lo de siempre). Con una fuente que solo trae sus cuadros reales (<c>ICaptureSource.DeliversSparseVideo</c>: NDI), el preview
    /// pinta los que llegan —4 por segundo de un grafismo que declara 60— mientras se graba a la cadencia declarada: el contador de
    /// frames, el FPS OUT y la alarma de frames perdidos hablan de esa cadencia, como cuando el receptor servía un cuadro por periodo,
    /// y salen del tiempo de medios que lleva el proceso (<c>out_time</c>, que FFmpeg 7+ toma de la salida más atrasada). El fps de
    /// FFmpeg (<see cref="RecorderStats.InputFps"/>) sigue siendo el de los cuadros que llegan.
    /// </summary>
    public double? MediaFrameRate { get; set; }

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
        int perSource = Math.Max(1, FramesPerSourceFrame);
        double fps = GetDouble("fps") / perSource;
        long frame = GetLong("frame") / perSource;
        long drop = GetLong("drop_frames");
        long dup = GetLong("dup_frames");
        long bitrate = (long)(GetLeadingDouble("bitrate") * 1000); // "2046.7kbits/s" → bps
        // Tiempo real transcurrido de la salida. 'out_time_us' es el campo moderno; algunos builds solo
        // emiten 'out_time_ms' (que, por un histórico de FFmpeg, también está en microsegundos). Al arrancar
        // FFmpeg escribe «N/A»: para el timecode vale 0, pero quien mide el ritmo necesita saber que aún no hay dato.
        bool knownTime = TryGetLong("out_time_us", out long outTimeUs);
        if (!knownTime || outTimeUs == 0) knownTime = TryGetLong("out_time_ms", out outTimeUs) || knownTime;
        if (!knownTime) outTimeUs = 0;
        if (MediaFrameRate is { } mediaRate && mediaRate > 0)
            frame = (long)Math.Floor(Math.Max(0, outTimeUs) * mediaRate / 1_000_000);

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
            // El ritmo de AHORA, no la media desde el arranque; mientras el proceso es demasiado nuevo para medirlo, la media
            // de FFmpeg (que en un proceso recién nacido viene a ser lo mismo).
            OutputFps: InstantRate(frame) ?? fps,
            DroppedFrames: drop,
            DuplicatedFrames: dup,
            Bitrate: new Bitrate(bitrate),
            BufferHealth: bufferHealth,
            Timecode: tc,
            FrameCount: frame)
        {
            MediaTimeUs = knownTime ? outTimeUs : -1,
        };
    }

    /// <summary>Por debajo de este tramo entre dos bloques no se mide el ritmo (dos bloques que llegan de golpe).</summary>
    private static readonly TimeSpan MinRateSpan = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Frames por segundo de pared en el último <see cref="RateWindow"/>: el contador <c>frame</c> de este bloque menos el del
    /// bloque de referencia (el más reciente con al menos esa antigüedad), entre el tiempo que los separa. Null mientras el
    /// proceso actual no tenga dos bloques separados al menos <see cref="MinRateSpan"/>. El contador acumulado no se toca: es el
    /// que usa la alarma de frames perdidos (<c>SaturationDetector</c>).
    /// </summary>
    private double? InstantRate(long frame)
    {
        var now = _clock();
        // Proceso nuevo (relanzamiento, pieza nueva, Grabar/Detener): su contador empieza de cero y lo anterior no cuenta.
        if (_recent.Count > 0 && frame < _recent[^1].Frame) _recent.Clear();
        _recent.Add((now, frame));
        while (_recent.Count > 2 && now - _recent[1].At >= RateWindow) _recent.RemoveAt(0);
        var (at, reference) = _recent[0];
        var span = now - at;
        return span < MinRateSpan ? null : (frame - reference) / span.TotalSeconds;
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

    private long GetLong(string key) => TryGetLong(key, out var l) ? l : 0;

    private bool TryGetLong(string key, out long value)
    {
        value = 0;
        return _kv.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
