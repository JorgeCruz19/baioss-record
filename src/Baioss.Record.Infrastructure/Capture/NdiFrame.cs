using System.Buffers;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>Formato de los cuadros de vídeo que el receptor NDI sirve a FFmpeg: tamaño, formato de píxel de FFmpeg y cadencia
/// declarada (la de sus periodos).</summary>
internal sealed record NdiVideoFormat(int Width, int Height, string PixelFormat, int RateN, int RateD);

/// <summary>
/// Búfer alquilado del <see cref="ArrayPool{T}"/> + su longitud útil (Rent puede dar un array mayor): un cuadro de vídeo o un bloque
/// de audio que el receptor NDI sirve a FFmpeg. Un cuadro de vídeo puede estar a la vez en la cola (una o varias veces, si se repite)
/// y guardado como el último para repetirlo: vuelve al pool cuando lo suelta el último que lo tiene (la cola al escribirlo o
/// descartarlo, el receptor al llegar otro).
/// </summary>
internal sealed class NdiFrame
{
    private int _refs = 1;

    public NdiFrame(byte[] buffer, int length, NdiVideoFormat? format = null)
    {
        Buffer = buffer;
        Length = length;
        Format = format;
    }

    public byte[] Buffer { get; }
    public int Length { get; }

    /// <summary>Formato del cuadro de vídeo (null en un bloque de audio).</summary>
    public NdiVideoFormat? Format { get; }

    public NdiFrame Share() { Interlocked.Increment(ref _refs); return this; }
    public void Release() { if (Interlocked.Decrement(ref _refs) == 0) ArrayPool<byte>.Shared.Return(Buffer); }
}

/// <summary>Un cuadro de vídeo servido a FFmpeg, el periodo de la cadencia declarada que ocupa (contado por el receptor desde que
/// arrancó; cada proceso de FFmpeg lo ve desde su primer cuadro) y el instante de la fuente en que empieza ese periodo, si se conoce
/// (con él se alinea el comienzo del audio de cada proceso: ver <see cref="NdiAudioAligner"/>).</summary>
internal readonly record struct NdiVideoItem(NdiFrame Frame, long Period, TimeSpan? Source = null);
