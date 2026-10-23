using System.Globalization;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Application.Clips;

/// <summary>
/// Una pista de un archivo tal como la describe ffprobe. Las marcas (<see cref="Start"/>, <see cref="Duration"/>) son
/// segundos en la escala del propio archivo: las mismas que usa FFmpeg con <c>-seek_timestamp 1</c>.
/// </summary>
public sealed record MediaStreamInfo
{
    /// <summary>Índice de la pista en el archivo.</summary>
    public int Index { get; init; }
    /// <summary>«video» o «audio».</summary>
    public required string Type { get; init; }
    /// <summary>Nombre del códec según FFmpeg («h264», «hevc», «prores», «aac», «pcm_s24le»…).</summary>
    public required string Codec { get; init; }
    /// <summary>Perfil del códec («High», «HQ», «DNXHR HQ»…), si lo hay.</summary>
    public string? Profile { get; init; }
    public string? PixelFormat { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Cadencia del vídeo (r_frame_rate), o null en el audio.</summary>
    public FrameRate? FrameRate { get; init; }
    /// <summary>Orden de campos («progressive», «tt», «bb», «tb», «bt»), si se conoce.</summary>
    public string? FieldOrder { get; init; }
    /// <summary>Relación de aspecto del píxel (SAR) como «num:den» («1:1» o null = cuadrado).</summary>
    public string? SampleAspectRatio { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    /// <summary>Bits por segundo de la pista (null si el contenedor no lo dice).</summary>
    public long? BitRate { get; init; }
    /// <summary>Marca (s) de la primera muestra presentada.</summary>
    public double? Start { get; init; }
    public double? Duration { get; init; }
    /// <summary>Cuadros (vídeo) o paquetes (audio) que declara el contenedor.</summary>
    public long? Frames { get; init; }
    /// <summary>El vídeo reordena cuadros (B-frames): el orden de decodificación no es el de presentación.</summary>
    public bool HasBFrames { get; init; }

    /// <summary>Entrelazado según el contenedor (orden de campos distinto de «progressive»).</summary>
    public bool IsInterlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";
}

/// <summary>Lo que hay en un archivo de grabación: su vídeo (si tiene) y sus pistas de audio, con el origen de tiempos del
/// formato (<see cref="FormatStart"/>: la menor de las marcas iniciales de sus pistas, el origen que usa el demuxer concat).</summary>
public sealed record MediaDescription
{
    /// <summary>Nombre del formato según FFmpeg («mov,mp4,m4a,3gp,3g2,mj2», «matroska,webm», «mxf», «mpegts»…).</summary>
    public string? FormatName { get; init; }
    public double FormatStart { get; init; }
    public double FormatDuration { get; init; }
    public MediaStreamInfo? Video { get; init; }
    public IReadOnlyList<MediaStreamInfo> Audio { get; init; } = Array.Empty<MediaStreamInfo>();

    /// <summary>
    /// Lo que tiene que coincidir entre dos piezas para unirlas con el demuxer concat copiando o decodificando seguido: los
    /// códecs y sus parámetros. El demuxer toma la descripción de la PRIMERA pieza para todo: una pieza con otro tamaño u otro
    /// códec saldría ilegible o mal.
    /// </summary>
    public string Signature
    {
        get
        {
            var parts = new List<string>();
            if (Video is { } v)
                parts.Add(string.Create(CultureInfo.InvariantCulture,
                    $"v:{v.Codec}/{v.Profile}/{v.PixelFormat}/{v.Width}x{v.Height}/{v.FrameRate?.Numerator}:{v.FrameRate?.Denominator}/{v.FieldOrder}"));
            foreach (var a in Audio)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"a:{a.Codec}/{a.SampleRate}/{a.Channels}"));
            return string.Join("|", parts);
        }
    }
}
