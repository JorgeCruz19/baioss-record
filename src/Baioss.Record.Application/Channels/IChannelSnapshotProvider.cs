namespace Baioss.Record.Application.Channels;

/// <summary>
/// Instantáneas de BAJA RESOLUCIÓN del preview de un canal, para clientes remotos (el panel web) que no pueden recibir
/// el preview crudo de la aplicación (BGRA a decenas de MB/s). Es bajo demanda: mientras nadie pide imágenes no se
/// captura ni se codifica nada, así que no cuesta CPU ni red con el panel cerrado.
/// </summary>
public interface IChannelSnapshotProvider
{
    /// <summary>
    /// JPEG de un frame de preview del canal, reducido a <paramref name="width"/> píxeles de ancho (el alto conserva la
    /// proporción). Una imagen más reciente que <paramref name="maxAgeMs"/> se reutiliza (varios clientes comparten el
    /// trabajo); si no, se captura el SIGUIENTE frame. Quien pide a N imágenes por segundo pasa una fracción de su
    /// periodo, para no recibir dos veces la misma. <c>null</c> si el canal no tiene preview (canal simulado, entrada
    /// reasignándose) o no llega ningún frame a tiempo.
    /// </summary>
    Task<byte[]?> GetJpegAsync(Guid channelId, int width, int maxAgeMs = 400, CancellationToken ct = default);
}
