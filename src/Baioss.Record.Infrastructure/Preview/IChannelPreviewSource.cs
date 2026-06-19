namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Fuente de frames de preview de un canal para la capa de presentación. La abstrae de si los
/// frames vienen de un proceso de preview dedicado (<see cref="FfmpegPreviewEngine"/>, fuentes de
/// archivo) o del proceso de captura unificado que graba a la vez (<see cref="FfmpegChannelEngine"/>,
/// dispositivos en vivo). La UI enlaza su superficie a estos frames y medidores sin conocer el origen.
/// </summary>
public interface IChannelPreviewSource
{
    int FrameWidth { get; }
    int FrameHeight { get; }

    /// <summary>Se eleva por cada frame BGRA decodificado (en un hilo de fondo; la UI marshalea).</summary>
    event EventHandler<PreviewFrame>? FrameReady;

    /// <summary>Niveles true-peak L/R (dBFS) de la señal en vivo, para los medidores VU.</summary>
    event EventHandler<(double Left, double Right)>? AudioPeaksUpdated;
}
