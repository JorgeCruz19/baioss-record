using System.Globalization;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Application.Clips;

/// <summary>Un archivo de la grabación (un <c>Segment</c>: un segmento, o la pieza que siguió a una caída) con lo que dijo ffprobe.</summary>
public sealed record TimelinePiece
{
    public required Guid SegmentId { get; init; }
    public required int Index { get; init; }
    public required string FilePath { get; init; }
    /// <summary>Cuadros de vídeo de la pieza (en una grabación de solo audio, de la rejilla virtual de <see cref="RecordingTimeline.AudioOnlyRate"/>).</summary>
    public required long Frames { get; init; }
    public required MediaDescription Media { get; init; }
    /// <summary>Hora a la que empezó la pieza (la del archivo), para enseñar la hora del día de cada cuadro.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Marca (s) del primer cuadro de vídeo (o de la primera muestra de audio) en la escala del archivo.</summary>
    public double FirstStart => Media.Video?.Start ?? Media.Audio.FirstOrDefault()?.Start ?? Media.FormatStart;

    /// <summary>Del origen del formato al primer cuadro: el demuxer concat pone cada archivo a partir de su origen, así que es
    /// lo que hay que sumar a su posición en la unión para llegar al cuadro (21 ms en el primer segmento del muxer
    /// <c>segment</c>, por el cebado del AAC; 0 en los demás).</summary>
    public double LeadIn => FirstStart - Media.FormatStart;

    /// <summary>Marca (s) del final del primer audio en la escala del archivo, o null sin audio.</summary>
    public double? AudioEnd => Media.Audio.FirstOrDefault() is { Start: { } s, Duration: { } d } ? s + d : null;
}

/// <summary>Por qué un archivo de la grabación no entra en la línea de tiempo.</summary>
public enum SkipReason
{
    /// <summary>No está en el disco.</summary>
    Missing,
    /// <summary>FFmpeg no lo lee: dañado, o un MP4 sin índice que se cortó en una caída del proceso.</summary>
    Unreadable,
    /// <summary>Tiene otra cadencia de cuadro que el resto (la señal cambió de formato a mitad).</summary>
    OtherFrameRate,
    /// <summary>No tiene ni un cuadro.</summary>
    Empty,
}

/// <summary>Un archivo de la grabación que la línea de tiempo se salta, y por qué.</summary>
public sealed record SkippedPiece(string FilePath, SkipReason Reason);

/// <summary>
/// La grabación entera como una sola línea de tiempo en CUADROS: las piezas en orden, una tras otra. Cuadro global <c>g</c> =
/// cuadro <c>g − inicio</c> de la pieza que lo contiene. Todo el corte se hace en cuadros (enteros) y solo al final se pasa a
/// segundos para FFmpeg, con aritmética racional de la cadencia (29,97 = 30000/1001): así nunca se pierde un cuadro por redondeo.
/// </summary>
public sealed class RecordingTimeline
{
    /// <summary>Rejilla de una grabación sin vídeo: 25 «cuadros» por segundo para mover y marcar.</summary>
    public static readonly FrameRate AudioOnlyRate = FrameRate.P25;

    private readonly long[] _starts;

    public RecordingTimeline(Guid sessionId, Guid channelId, FrameRate rate, IReadOnlyList<TimelinePiece> pieces,
        IReadOnlyList<SkippedPiece>? skipped = null)
    {
        if (rate.Numerator <= 0 || rate.Denominator <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        if (pieces.Count == 0) throw new ArgumentException("Una línea de tiempo necesita al menos una pieza.", nameof(pieces));
        SessionId = sessionId;
        ChannelId = channelId;
        Rate = rate;
        Pieces = pieces;
        Skipped = skipped ?? Array.Empty<SkippedPiece>();
        _starts = new long[pieces.Count];
        long acc = 0;
        for (int i = 0; i < pieces.Count; i++) { _starts[i] = acc; acc += Math.Max(0, pieces[i].Frames); }
        TotalFrames = acc;
    }

    public Guid SessionId { get; }
    public Guid ChannelId { get; }
    public FrameRate Rate { get; }
    public IReadOnlyList<TimelinePiece> Pieces { get; }
    /// <summary>Archivos de la grabación que no están en la línea de tiempo (ver <see cref="SkipReason"/>): se avisa de ellos.</summary>
    public IReadOnlyList<SkippedPiece> Skipped { get; }
    public long TotalFrames { get; }

    public bool HasVideo => Pieces[0].Media.Video is not null;
    public bool HasAudio => Pieces[0].Media.Audio.Count > 0;

    /// <summary>Lo que dicen los archivos de la primera pieza (códecs, tamaño): de ahí salen el contenedor y el codificador del clip.</summary>
    public MediaDescription Media => Pieces[0].Media;

    /// <summary>Todas las piezas tienen los mismos parámetros: se pueden leer seguidas con el demuxer concat.</summary>
    public bool Uniform => Pieces.All(p => p.Media.Signature == Pieces[0].Media.Signature);

    /// <summary>Primer cuadro global de la pieza <paramref name="piece"/>.</summary>
    public long PieceStart(int piece) => _starts[piece];

    /// <summary>Duración total.</summary>
    public TimeSpan Duration => TimeOf(TotalFrames);

    /// <summary>Lo que dura un cuadro.</summary>
    public TimeSpan FrameDuration => TimeOf(1);

    /// <summary>Instante del cuadro <paramref name="frame"/> desde el principio de la grabación (exacto al tick de 100 ns).</summary>
    public TimeSpan TimeOf(long frame)
        => TimeSpan.FromTicks((long)Math.Round((decimal)frame * Rate.Denominator * TimeSpan.TicksPerSecond / Rate.Numerator, MidpointRounding.AwayFromZero));

    /// <summary>Segundos (exactos en racional, como double) desde el principio hasta el cuadro <paramref name="frame"/>.</summary>
    public double SecondsOf(long frame) => (double)((decimal)frame * Rate.Denominator / Rate.Numerator);

    /// <summary>El cuadro que se ve en el instante <paramref name="time"/> (el último que empieza en él o antes).</summary>
    public long FrameAt(TimeSpan time)
    {
        if (time <= TimeSpan.Zero) return 0;
        // Medio microsegundo de gracia: un instante calculado con TimeOf, o tecleado con milisegundos, cae en su cuadro.
        decimal frames = ((decimal)time.Ticks + 5) * Rate.Numerator / ((decimal)Rate.Denominator * TimeSpan.TicksPerSecond);
        return (long)Math.Floor(frames);
    }

    /// <summary>El primer cuadro que empieza en <paramref name="time"/> o después (para el FINAL de un tramo: lo que empieza
    /// antes se incluye).</summary>
    public long FrameFrom(TimeSpan time)
    {
        if (time <= TimeSpan.Zero) return 0;
        decimal frames = ((decimal)time.Ticks - 5) * Rate.Numerator / ((decimal)Rate.Denominator * TimeSpan.TicksPerSecond);
        return (long)Math.Ceiling(frames);
    }

    /// <summary>Pieza que contiene el cuadro <paramref name="frame"/> y su cuadro dentro de ella.</summary>
    public (int Piece, long Local) Locate(long frame)
    {
        if (frame < 0) frame = 0;
        int lo = 0, hi = _starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= frame) lo = mid; else hi = mid - 1;
        }
        // Las piezas vacías (0 cuadros) no contienen nada: se salta a la siguiente que sí.
        while (lo < _starts.Length - 1 && frame >= _starts[lo] + Pieces[lo].Frames) lo++;
        return (lo, frame - _starts[lo]);
    }

    /// <summary>Primer cuadro del segundo <paramref name="second"/> (el primero que empieza en él o después).</summary>
    public long FirstFrameOfSecond(long second)
        => (second * Rate.Numerator + Rate.Denominator - 1) / Rate.Denominator;

    /// <summary>
    /// «HH:MM:SS:FF» del cuadro: HH:MM:SS es el tiempo transcurrido de verdad (a 29,97 no se adelanta como un timecode sin
    /// drop-frame) y FF el cuadro dentro de ese segundo. Cada cuadro tiene uno distinto y <see cref="TryParse"/> lo deshace.
    /// </summary>
    public string Timecode(long frame)
    {
        if (frame < 0) frame = 0;
        long second = frame * Rate.Denominator / Rate.Numerator;
        long ff = frame - FirstFrameOfSecond(second);
        return string.Create(CultureInfo.InvariantCulture, $"{second / 3600:00}:{second / 60 % 60:00}:{second % 60:00}:{ff:00}");
    }

    /// <summary>
    /// Una posición escrita por una persona o un sistema: segundos («75.5»), «HH:MM:SS(.fff)», «MM:SS(.fff)» o un timecode
    /// «HH:MM:SS:FF» (también con «;»). Devuelve el cuadro que EMPIEZA en ella: con <paramref name="end"/>, el primero que
    /// empieza en ella o después (lo de antes entra en el tramo); si no, el que se ve en ella. Un timecode es siempre su cuadro.
    /// </summary>
    public bool TryParse(string? text, bool end, out long frame)
    {
        frame = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var parts = s.Split(':', ';');
        if (parts.Length == 4)
        {
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sec)
                || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ff)
                || m >= 60 || sec >= 60)
                return false;
            long second = h * 3600 + m * 60 + sec;
            long first = FirstFrameOfSecond(second);
            if (ff >= FirstFrameOfSecond(second + 1) - first) return false; // ese segundo no tiene tantos cuadros
            frame = first + ff;
            return true;
        }
        if (parts.Length > 3) return false;
        decimal seconds = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!decimal.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            if (i > 0 && v >= 60) return false; // minutos y segundos de «HH:MM:SS» / «MM:SS»
            seconds = seconds * 60 + v;
        }
        var time = TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        frame = end ? FrameFrom(time) : FrameAt(time);
        return true;
    }

    /// <summary>Hora del día (de la pieza) del cuadro, si se sabe a qué hora empezó su pieza.</summary>
    public DateTimeOffset? WallClockOf(long frame)
    {
        var (piece, local) = Locate(frame);
        return Pieces[piece].StartedAt is { } started ? started + TimeOf(local) : null;
    }
}
