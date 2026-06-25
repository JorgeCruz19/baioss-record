using Baioss.Record.Domain;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Cadena de degradación del codificador de vídeo. Cuando un codificador por hardware no puede ABRIR
/// (sesiones NVENC agotadas, driver/DLL ausente, GPU sin soporte para ese códec), propone el siguiente
/// codificador a probar para que ese canal siga grabando en lugar de fallar. Orden de preferencia:
/// NVIDIA NVENC → Intel QuickSync → AMD AMF → CPU (libx264/libx265, siempre disponible). Es iterativa: el
/// motor reintenta con el resultado; si ese tampoco abre, vuelve a llamar y baja un escalón más.
///
/// Los codificadores que ya corren en CPU (libx264/libx265) o intra (ProRes, DNxHD/HR, MPEG-2) no
/// degradan: no dependen de la GPU y deberían abrir siempre. El decode se fuerza por software al degradar
/// (lo hace el motor poniendo <see cref="HwAccel.None"/>), así un frame nunca queda atrapado en una GPU
/// de distinta familia que el nuevo codificador no podría tomar.
/// </summary>
public static class EncoderFallbackChain
{
    /// <summary>
    /// Siguiente codificador a probar tras un fallo de APERTURA del actual, o <c>null</c> si no hay
    /// alternativa (ya es CPU/intra: último recurso).
    /// </summary>
    public static VideoCodec? Next(VideoCodec current) => current switch
    {
        // H.264 por hardware: NVENC → QuickSync → AMF → CPU.
        VideoCodec.H264Nvenc => VideoCodec.H264Qsv,
        VideoCodec.H264Qsv   => VideoCodec.H264Amf,
        VideoCodec.H264Amf   => VideoCodec.H264x264,

        // HEVC/AV1 por NVENC: sin equivalente por hardware en el catálogo → directo a CPU. HEVC mantiene el
        // códec (libx265); AV1 no tiene codificador CPU práctico configurado, así que cae a H.264 (libx264):
        // cambia el códec, pero «grabar algo legible» supera a «no grabar».
        VideoCodec.HevcNvenc => VideoCodec.H265x265,
        VideoCodec.Av1Nvenc  => VideoCodec.H264x264,

        // Ya es CPU/intra (libx264/265, ProRes, DNxHD/HR, MPEG-2): sin degradación.
        _ => null,
    };
}
