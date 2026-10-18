using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Cómo se enmarcan los elementos de un flujo de <see cref="NdiFeed{T}"/> para el proceso de FFmpeg conectado: un objeto por
/// conexión, con su estado (las cabeceras ya enviadas, el origen de sus marcas). Un proceso nuevo, o uno relanzado que vuelve a
/// conectar, recibe uno nuevo.
/// </summary>
internal interface INdiFeedFraming<in T>
{
    /// <summary>Lo que va en el socket justo antes de los datos de <paramref name="item"/> (la primera vez, también las cabeceras del
    /// contenedor); false si el elemento no es para este proceso y no se escribe.</summary>
    bool TryPrefix(T item, out ReadOnlyMemory<byte> prefix);
}

/// <summary>
/// El vídeo NDI en NUT para un proceso de FFmpeg (ver <see cref="NutRawVideoWriter"/>): las cabeceras con el formato de su primer
/// cuadro y cada cuadro con su periodo contado desde ese primero, que es el 0 (FFmpeg empieza cada entrada en 0 de todos modos).
/// No le escribe un cuadro de otro formato que el de sus cabeceras —la fuente cambió en caliente: este proceso se queda con el
/// último cuadro bueno hasta que el canal se reconstruya, en vez de leer datos de otro tamaño— ni uno que no avance en el tiempo
/// (FFmpeg los ordena por la marca).
/// </summary>
internal sealed class NdiNutFraming : INdiFeedFraming<NdiVideoItem>
{
    private NutRawVideoWriter? _writer;
    private NdiVideoFormat? _format;
    private long _first, _last;
    private readonly byte[] _header = new byte[NutRawVideoWriter.MaxFrameHeaderBytes];

    /// <summary>Cuadros no escritos a este proceso: de otro formato o sin avanzar en el tiempo.</summary>
    public long Skipped { get; private set; }

    public bool TryPrefix(NdiVideoItem item, out ReadOnlyMemory<byte> prefix)
    {
        prefix = default;
        if (item.Frame.Format is not { } format) { Skipped++; return false; }
        if (_writer is null)
        {
            _writer = new NutRawVideoWriter(format.Width, format.Height, format.PixelFormat, format.RateN, format.RateD);
            _format = format;
            _first = _last = item.Period;
            var first = new byte[_writer.Preamble.Length + NutRawVideoWriter.MaxFrameHeaderBytes];
            _writer.Preamble.CopyTo(first, 0);
            int n = _writer.WriteFrameHeader(first.AsSpan(_writer.Preamble.Length), 0, item.Frame.Length);
            prefix = first.AsMemory(0, _writer.Preamble.Length + n);
            return true;
        }
        if (format != _format || item.Period <= _last) { Skipped++; return false; }
        _last = item.Period;
        int m = _writer.WriteFrameHeader(_header, item.Period - _first, item.Frame.Length);
        prefix = _header.AsMemory(0, m);
        return true;
    }
}
