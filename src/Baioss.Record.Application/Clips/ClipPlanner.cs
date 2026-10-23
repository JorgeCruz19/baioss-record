using System.Globalization;
using System.Text;

namespace Baioss.Record.Application.Clips;

/// <summary>Un archivo de la lista del demuxer concat y lo que se declara que dura.</summary>
public sealed record ConcatEntry(string FilePath, double Duration);

/// <summary>
/// Lo que FFmpeg lee para un tramo de piezas seguidas (<see cref="FirstPiece"/>..<see cref="LastPiece"/>): un archivo, o una
/// lista del demuxer concat con todos; y dónde cae cada cuadro global en la escala de esa entrada, que es la que entiende
/// <c>-ss</c> con <c>-seek_timestamp 1</c>.
///
/// <para>Por qué así (medido con FFmpeg 8.0): el <c>-ss</c> normal es relativo al inicio del formato, y el del concat no es
/// el de su primer archivo: con B-frames y el cebado del AAC (marcas negativas) el concat decía −0,064 s y todo corte salía
/// UN CUADRO antes. Con <c>-seek_timestamp 1</c> la marca es absoluta: en una pieza sola, la del propio archivo; en el concat,
/// cada archivo empieza en la suma de lo que se declaró que duraban los anteriores, contado desde el origen de su formato. Por
/// eso cada archivo se declara con lo que hace falta para que su vídeo siga sin hueco ni solape al del anterior (la duración
/// del formato dejaba huecos de 5–10 ms en las uniones, y la del vídeo solo, un cuadro de desfase tras el primer segmento,
/// cuyo vídeo empieza 21 ms tarde por el cebado del audio). Si el audio de las dos piezas no casa con eso (piezas tras una
/// caída), se deja el hueco en el vídeo antes que solapar el audio.</para>
/// </summary>
public sealed class TimelineInput
{
    private readonly RecordingTimeline _timeline;
    private readonly long[] _offsetsUs; // inicio de cada pieza del tramo en la escala del concat (µs, como lo cuenta FFmpeg)

    public TimelineInput(RecordingTimeline timeline, int firstPiece, int lastPiece)
    {
        if (firstPiece < 0 || lastPiece >= timeline.Pieces.Count || firstPiece > lastPiece)
            throw new ArgumentOutOfRangeException(nameof(firstPiece));
        _timeline = timeline;
        FirstPiece = firstPiece;
        LastPiece = lastPiece;
        int count = lastPiece - firstPiece + 1;
        _offsetsUs = new long[count];
        var entries = new List<ConcatEntry>(count);
        double fps = timeline.Rate.Value;
        long offset = 0;
        for (int i = firstPiece; i <= lastPiece; i++)
        {
            var p = timeline.Pieces[i];
            _offsetsUs[i - firstPiece] = offset;
            double videoEnd = p.LeadIn + p.Frames / fps; // desde el origen del formato hasta el final del último cuadro
            double declared;
            if (i < lastPiece)
            {
                var next = timeline.Pieces[i + 1];
                declared = videoEnd - next.LeadIn;
                // El audio de la pieza no debe solapar el de la siguiente más de un paquete de AAC. En las del muxer segment
                // difieren menos de 1 ms (lo que sobra de un paquete de 21 ms) y ahí manda la rejilla de cuadros.
                if (p.AudioEnd is { } aEnd && next.Media.Audio.FirstOrDefault()?.Start is { } nextAudio)
                {
                    double audioWants = (aEnd - p.Media.FormatStart) - (nextAudio - next.Media.FormatStart);
                    if (audioWants - declared > AudioOverlapTolerance) declared = audioWants;
                }
            }
            else declared = Math.Max(videoEnd, p.AudioEnd is { } aEnd ? aEnd - p.Media.FormatStart : 0);
            long us = (long)Math.Round(Math.Max(0, declared) * 1e6, MidpointRounding.AwayFromZero);
            entries.Add(new ConcatEntry(p.FilePath, us / 1e6));
            offset += us;
        }
        Entries = count > 1 ? entries : Array.Empty<ConcatEntry>();
    }

    /// <summary>Solape de audio entre dos piezas que se tolera para que el vídeo siga en su rejilla: algo más de un paquete de AAC.</summary>
    public const double AudioOverlapTolerance = 0.025;

    public int FirstPiece { get; }
    public int LastPiece { get; }
    public bool IsConcat => LastPiece > FirstPiece;
    /// <summary>La lista del concat (vacía si es un solo archivo).</summary>
    public IReadOnlyList<ConcatEntry> Entries { get; }
    /// <summary>El archivo, si es uno solo.</summary>
    public string? SingleFile => IsConcat ? null : _timeline.Pieces[FirstPiece].FilePath;

    /// <summary>Primer cuadro global del tramo.</summary>
    public long FirstFrame => _timeline.PieceStart(FirstPiece);
    /// <summary>Cuadro global siguiente al último del tramo.</summary>
    public long EndFrame => _timeline.PieceStart(LastPiece) + _timeline.Pieces[LastPiece].Frames;

    /// <summary>Marca (s) del cuadro global <paramref name="frame"/> en la escala de esta entrada. Admite el cuadro siguiente al
    /// último (<see cref="EndFrame"/>): el final del tramo.</summary>
    public double TimeOf(long frame)
    {
        if (frame < FirstFrame || frame > EndFrame) throw new ArgumentOutOfRangeException(nameof(frame));
        // La última pieza del tramo que empieza en el cuadro o antes: el final exacto de una pieza es el principio de la siguiente.
        int piece = LastPiece;
        while (piece > FirstPiece && frame < _timeline.PieceStart(piece)) piece--;
        var p = _timeline.Pieces[piece];
        long local = frame - _timeline.PieceStart(piece);
        double inPiece = (double)((decimal)local * _timeline.Rate.Denominator / _timeline.Rate.Numerator);
        return IsConcat
            ? _offsetsUs[piece - FirstPiece] / 1e6 + p.LeadIn + inPiece
            : p.FirstStart + inPiece;
    }

    /// <summary>De una marca de un archivo del tramo (las de ffprobe de ese archivo) a la escala de esta entrada.</summary>
    public double ToInputTime(int piece, double fileTime)
        => IsConcat ? _offsetsUs[piece - FirstPiece] / 1e6 + fileTime - _timeline.Pieces[piece].Media.FormatStart : fileTime;

    /// <summary>El texto de la lista del concat (UTF-8 sin BOM): versión 1.0, cada archivo entre comillas simples (una comilla
    /// del nombre se escribe cerrando, «\'» y abriendo) y su duración, para que el demuxer pueda buscar sin abrirlos todos.</summary>
    public string ConcatList()
    {
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var e in Entries)
        {
            sb.Append("file '").Append(e.FilePath.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
            sb.Append("duration ").Append(e.Duration.ToString("0.000000", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// El corte de un clip: de qué piezas sale, qué lee FFmpeg y desde y hasta qué marca. <see cref="InFrame"/> es el primer cuadro
/// del clip y <see cref="OutFrame"/> el siguiente al último (no entra).
/// </summary>
public sealed record ClipPlan(TimelineInput Input, long InFrame, long OutFrame, double Start, double End)
{
    public long Frames => OutFrame - InFrame;
}

/// <summary>Decide el corte de un clip en una grabación (puro: sin disco ni FFmpeg, para poder probarlo).</summary>
public static class ClipPlanner
{
    /// <summary>
    /// El corte del tramo [<paramref name="inFrame"/>, <paramref name="outFrame"/>) de la grabación. Si cruza varias piezas, se
    /// unen con el demuxer concat (y por eso tienen que tener los mismos parámetros). Lanza <see cref="ClipException"/> si el
    /// tramo no cabe en la grabación o las piezas no se pueden unir.
    /// </summary>
    public static ClipPlan Plan(RecordingTimeline timeline, long inFrame, long outFrame)
    {
        if (inFrame < 0 || outFrame > timeline.TotalFrames || inFrame >= timeline.TotalFrames)
            throw new ClipException(ClipError.OutOfRange,
                string.Create(CultureInfo.InvariantCulture, $"{inFrame}-{outFrame} / {timeline.TotalFrames}"));
        if (outFrame <= inFrame)
            throw new ClipException(ClipError.Empty, string.Create(CultureInfo.InvariantCulture, $"{inFrame}-{outFrame}"));

        int first = timeline.Locate(inFrame).Piece;
        int last = timeline.Locate(outFrame - 1).Piece;
        for (int i = first + 1; i <= last; i++)
            if (timeline.Pieces[i].Media.Signature != timeline.Pieces[first].Media.Signature)
                throw new ClipException(ClipError.PiecesDiffer,
                    $"{System.IO.Path.GetFileName(timeline.Pieces[first].FilePath)} ≠ {System.IO.Path.GetFileName(timeline.Pieces[i].FilePath)}");

        var input = new TimelineInput(timeline, first, last);
        return new ClipPlan(input, inFrame, outFrame, input.TimeOf(inFrame), input.TimeOf(outFrame));
    }
}
