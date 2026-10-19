using System.Buffers;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Alinea el comienzo del audio de cada proceso de FFmpeg con su primer cuadro de vídeo, por las marcas NDI de la fuente. FFmpeg lee
/// el vídeo (en NUT, cada cuadro con la marca de su periodo) y el audio (crudo: cuenta muestras desde la primera que recibe) por dos
/// entradas, y cada una empieza en 0: la muestra 0 tiene que ser la del instante de la fuente en que empieza el periodo 0 del vídeo.
/// Antes el audio empezaba en el primer bloque que llegaba tras conectarse, y lo que el vídeo llega más tarde que su audio (el SDK
/// lo descomprime) más lo que tarda FFmpeg en pasar de una entrada a otra quedaban como desfase fijo de toda la grabación: el pitido
/// de una marca 35–135 ms por delante de su destello, más con el equipo cargado y en cualquier contenedor (medido el 2026-10-18).
/// <para>Guarda los últimos bloques de audio real con su marca (el instante de la fuente de su primera muestra, que el SDK pone al
/// enviarlo, como al vídeo) y, al conectarse el audio de un proceso, le da lo que hay desde ese instante: el bloque que lo contiene,
/// recortado, y los siguientes. Si el instante aún no ha llegado, descarta lo que llegue antes de él (<see cref="Admit"/>); si es
/// anterior a lo guardado, empieza con silencio hasta lo guardado. Sin marcas, o si audio y vídeo no cuadran (más de
/// <see cref="MaxSkew"/>), no alinea: lo de siempre. Puro salvo el pool de búferes; el receptor lo usa bajo su candado.</para>
/// </summary>
internal sealed class NdiAudioAligner
{
    private readonly Queue<(TimeSpan Start, int Samples, NdiFrame Block)> _history = new();
    private (TimeSpan Start, int Samples) _newest;
    private int _sampleRate, _channels;
    private TimeSpan? _startAt; // el arranque del proceso conectado, si su audio aún no había llegado

    /// <summary>Cuánto audio se guarda: de sobra para lo que el vídeo va por detrás (décimas de segundo).</summary>
    public TimeSpan Keep { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Lo más que pueden separarse el instante de arranque del vídeo y el audio que hay para creer que son del mismo reloj.</summary>
    public TimeSpan MaxSkew { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Qué hizo <see cref="StartAt"/>: nada (sin marcas o sin cuadrar), recortar lo guardado, esperar a que llegue el
    /// instante, o rellenar con silencio el principio; y cuánto (el recorte, la espera o el relleno).</summary>
    public enum Outcome { NotAligned, FromHistory, Waiting, Padded }

    /// <summary>Guarda un bloque de audio real que acaba de llegar (si trae marca). El bloque lo sigue usando el llamador: aquí se toma
    /// una referencia propia, que se suelta al olvidarlo.</summary>
    public void Remember(TimeSpan? start, NdiFrame block, int samples, int sampleRate, int channels)
    {
        if (start is not { } at || samples <= 0 || sampleRate <= 0 || channels <= 0) { Forget(); return; }
        if (sampleRate != _sampleRate || channels != _channels) { Forget(); _sampleRate = sampleRate; _channels = channels; }
        _history.Enqueue((at, samples, block.Share()));
        _newest = (at, samples);
        while (_history.Count > 1 && at - _history.Peek().Start > Keep) _history.Dequeue().Block.Release();
    }

    /// <summary>Olvida lo guardado (la fuente dejó de traer marcas, cambió de formato de audio o pasó a silencio) y cualquier espera.</summary>
    public void Forget()
    {
        while (_history.Count > 0) _history.Dequeue().Block.Release();
        _startAt = null;
    }

    /// <summary>
    /// Se conectó el audio de un proceso cuyo vídeo empieza en <paramref name="videoStart"/> (el instante de la fuente de su periodo 0):
    /// devuelve los bloques que hay que darle ya, empezando en ese instante —cada uno con una referencia propia, para encolarlos—, y
    /// cómo quedó. Con <see cref="Outcome.Waiting"/> no hay nada que darle todavía: <see cref="Admit"/> recorta lo que llegue.
    /// </summary>
    public (List<NdiFrame> Blocks, Outcome Outcome, TimeSpan Amount) StartAt(TimeSpan? videoStart)
    {
        _startAt = null;
        var blocks = new List<NdiFrame>();
        if (videoStart is not { } start || _history.Count == 0 || _sampleRate <= 0) return (blocks, Outcome.NotAligned, TimeSpan.Zero);
        var oldest = _history.Peek();
        var newestEnd = _newest.Start + Duration(_newest.Samples);
        if (start < oldest.Start - MaxSkew || start > newestEnd + MaxSkew) return (blocks, Outcome.NotAligned, TimeSpan.Zero);

        if (start >= newestEnd)
        {
            // El audio de ese instante aún no ha llegado (va por detrás del vídeo): se esperará descartando lo anterior.
            _startAt = start;
            return (blocks, Outcome.Waiting, start - newestEnd);
        }
        if (start < oldest.Start)
        {
            // Más atrás de lo guardado: silencio hasta el primer bloque y luego todo.
            var pad = oldest.Start - start;
            blocks.Add(SilenceBlock(Samples(pad)));
            foreach (var h in _history) blocks.Add(h.Block.Share());
            return (blocks, Outcome.Padded, pad);
        }
        TimeSpan trimmed = TimeSpan.Zero;
        foreach (var h in _history)
        {
            var end = h.Start + Duration(h.Samples);
            if (end <= start) continue;
            if (h.Start < start)
            {
                int skip = Math.Min(h.Samples - 1, Samples(start - h.Start));
                blocks.Add(Tail(h.Block, skip, h.Samples));
                trimmed = start - h.Start;
            }
            else blocks.Add(h.Block.Share());
        }
        return (blocks, Outcome.FromHistory, trimmed);
    }

    /// <summary>
    /// Un bloque de audio real que llega en directo: lo que hay que encolar de él. El propio bloque, salvo si el proceso conectado
    /// espera un instante que aún no había llegado: hasta ese instante no se le da nada (null) y el bloque que lo contiene, recortado
    /// (una copia). Lo que no se devuelve lo sigue teniendo el llamador.
    /// </summary>
    public NdiFrame? Admit(TimeSpan? start, NdiFrame block, int samples)
    {
        if (_startAt is not { } wanted) return block;
        if (start is not { } at) { _startAt = null; return block; } // sin marca ya no hay con qué esperar
        var end = at + Duration(samples);
        if (end <= wanted) return null;
        _startAt = null;
        if (at >= wanted) return block;
        return Tail(block, Math.Min(samples - 1, Samples(wanted - at)), samples);
    }

    private TimeSpan Duration(int samples) => TimeSpan.FromTicks((long)Math.Round((double)samples * TimeSpan.TicksPerSecond / _sampleRate));

    private int Samples(TimeSpan t) => (int)Math.Round(t.TotalSeconds * _sampleRate);

    private int FrameBytes => _channels * sizeof(float);

    /// <summary>Copia del bloque desde la muestra <paramref name="skip"/>.</summary>
    private NdiFrame Tail(NdiFrame block, int skip, int samples)
    {
        int from = skip * FrameBytes, bytes = (samples - skip) * FrameBytes;
        var buf = ArrayPool<byte>.Shared.Rent(bytes);
        Buffer.BlockCopy(block.Buffer, from, buf, 0, bytes);
        return new NdiFrame(buf, bytes);
    }

    private NdiFrame SilenceBlock(int samples)
    {
        int bytes = samples * FrameBytes;
        var buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, bytes));
        Array.Clear(buf, 0, bytes);
        return new NdiFrame(buf, bytes);
    }
}
