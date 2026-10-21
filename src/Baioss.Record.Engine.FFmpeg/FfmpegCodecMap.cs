using Baioss.Record.Domain;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Traduce los enums de dominio a nombres de encoder/muxer de FFmpeg.</summary>
public static class FfmpegCodecMap
{
    public static string VideoEncoder(VideoCodec codec) => codec switch
    {
        VideoCodec.H264Nvenc => "h264_nvenc",
        VideoCodec.HevcNvenc => "hevc_nvenc",
        VideoCodec.Av1Nvenc  => "av1_nvenc",
        VideoCodec.H264x264  => "libx264",
        VideoCodec.H265x265  => "libx265",
        VideoCodec.H264Qsv   => "h264_qsv",  // Intel QuickSync (GPU integrada)
        VideoCodec.H264Amf   => "h264_amf",  // AMD AMF (GPU integrada)
        VideoCodec.HevcQsv   => "hevc_qsv",  // Intel QuickSync HEVC (GPU integrada)
        // prores_aw y no prores_ks: en un i7-8700 prores_ks no llega a tiempo real (1080i29.97 HQ a 0,8–1,2× con los 12
        // hilos; Proxy/LT aún más lentos) y una captura en vivo perdía cuadros; prores_aw da 5–6× (3× en 4444/XQ), con los
        // seis perfiles y DCT entrelazada. Medido con una DeckLink, ver docs/08-presets.md.
        VideoCodec.ProRes    => "prores_aw",
        VideoCodec.DnxHd     => "dnxhd", // DNxHD clásico: el bitrate elige el CID (ver FfmpegArgumentBuilder.DnxhdBitrateMbps)
        VideoCodec.DnxHr     => "dnxhd", // DNxHR vía -profile dnxhr_hq
        VideoCodec.Mpeg2Video => "mpeg2video",
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
    };

    public static string AudioEncoder(AudioCodec codec) => codec switch
    {
        AudioCodec.Pcm    => "pcm_s24le",
        AudioCodec.Aac    => "aac",
        AudioCodec.FdkAac => "libfdk_aac",
        AudioCodec.Opus   => "libopus",
        AudioCodec.Mp3    => "libmp3lame",
        AudioCodec.Mp2    => "mp2",
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
    };

    /// <summary>Argumento de <c>-pix_fmt</c>, o <c>null</c> para dejar el predeterminado del códec.</summary>
    public static string? PixelFormatArg(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Yuv420p     => "yuv420p",
        PixelFormat.Yuv422p     => "yuv422p",
        PixelFormat.Yuv420p10le => "yuv420p10le",
        PixelFormat.Yuv422p10le => "yuv422p10le",
        PixelFormat.Yuv444p10le => "yuv444p10le",
        _ => null // Auto
    };

    /// <summary>
    /// Encoder de audio compatible con el contenedor. El PCM se graba como AAC en MP4 y TS, donde no hay un PCM que lean los
    /// editores: en MP4, FFmpeg 8.0 lo escribe como «ipcm» (ISO/IEC 23003-5), que casi ninguno lee, y en TS haría falta SMPTE
    /// 302M. En MOV va tal cual, como audio QuickTime: «in24» (descripción de sonido v1 con «enda», little-endian), o «lpcm» v2
    /// por encima de 65535 Hz y, desde FFmpeg 2026-05, con más de 2 canales. FFmpeg lo relee bit a bit con 1 a 16 canales, varias
    /// pistas, fMP4, el remux del Detener, segmentos, un corte del proceso y de 10 Hz a 768 kHz (medido el 2026-10-21 con FFmpeg
    /// 8.0 y el incluido, ver docs/08-presets.md).
    /// </summary>
    public static string EffectiveAudioEncoder(AudioCodec codec, ContainerFormat container) =>
        codec == AudioCodec.Pcm && container is ContainerFormat.Mp4 or ContainerFormat.Ts
            ? "aac"
            : AudioEncoder(codec);

    /// <summary>True si el audio se escribe sin pérdida (PCM) en ese contenedor, una vez aplicada la promoción a AAC.</summary>
    public static bool IsPcmAudio(AudioCodec codec, ContainerFormat container) =>
        EffectiveAudioEncoder(codec, container).StartsWith("pcm", StringComparison.Ordinal);

    /// <summary>
    /// Canales que caben en UNA pista con el codificador efectivo. PCM no tiene límite práctico (16 embebidos de sobra);
    /// MP2 y MP3 solo admiten mono/estéreo —y FFmpeg NO falla si se le da más: inserta un remuestreo y MEZCLA todo en el
    /// estéreo en silencio (medido)—; AAC, FDK-AAC y Opus llegan a 8.
    /// </summary>
    public static int MaxAudioChannels(AudioCodec codec, ContainerFormat container) =>
        EffectiveAudioEncoder(codec, container) switch
        {
            "mp2" or "libmp3lame" => 2,
            var e when e.StartsWith("pcm", StringComparison.Ordinal) => int.MaxValue,
            _ => 8,
        };

    /// <summary>Contenedores que solo admiten UN flujo de audio: con varias pistas el muxer aborta («wav muxer does not
    /// support more than one stream of type audio», «Exactly one MP3 audio stream is required») y no se graba nada.</summary>
    public static bool SingleAudioStream(ContainerFormat container) =>
        container is ContainerFormat.Wav or ContainerFormat.Mp3Audio;

    public static (string Muxer, string Extension) Container(ContainerFormat format) => format switch
    {
        ContainerFormat.Mp4 => ("mp4", "mp4"),
        ContainerFormat.Mov => ("mov", "mov"),
        ContainerFormat.Mxf => ("mxf", "mxf"),
        ContainerFormat.Mkv => ("matroska", "mkv"),
        ContainerFormat.Ts  => ("mpegts", "ts"),
        ContainerFormat.Avi => ("avi", "avi"),
        ContainerFormat.ProgramStream => ("mpeg", "mpg"),
        ContainerFormat.Wav => ("wav", "wav"),
        ContainerFormat.Mp3Audio => ("mp3", "mp3"),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>True si el encoder corre en GPU (afecta a la cadena de filtros _cuda).</summary>
    public static bool IsGpuEncoder(VideoCodec codec) =>
        codec is VideoCodec.H264Nvenc or VideoCodec.HevcNvenc or VideoCodec.Av1Nvenc;

    /// <summary>
    /// True si el contenedor guarda la REORDENACIÓN de ese códec: cuándo se presenta cada cuadro, no solo en qué orden se
    /// decodifica. AVI no guarda marcas de tiempo (cada cuadro va en su posición, en orden de decodificación, y quien lo lee lo
    /// presenta cuando el decodificador lo suelta) y el MXF de FFmpeg solo guarda la de MPEG-2 (el desfase temporal de su
    /// índice), no la de H.264. Ahí un códec que reordena (<see cref="ReordersFrames"/>) deja el vídeo tarde respecto al audio
    /// tantos cuadros como reordena: medido con FFmpeg 8.0 y una fuente con un destello y un pitido por segundo, 2 cuadros con
    /// x264 (80 ms a 25 fps) y 1 con QuickSync, en AVI y en MXF; en MP4, MOV, MKV, TS y PS, ninguno.
    /// </summary>
    public static bool KeepsReordering(ContainerFormat container, VideoCodec codec) => container switch
    {
        ContainerFormat.Avi => false,
        ContainerFormat.Mxf => codec is VideoCodec.Mpeg2Video,
        _ => true,
    };

    /// <summary>
    /// True si el codificador reordena cuadros (B-frames) salvo que se le pida <c>-bf 0</c>: x264 y x265 por su preset,
    /// QuickSync y AMF por lo que decida el driver. NVENC se pide siempre sin ellos, el MPEG-2 de FFmpeg no los usa si no se le
    /// piden y ProRes y DNxHD/HR son intra.
    /// </summary>
    public static bool ReordersFrames(VideoCodec codec) =>
        codec is VideoCodec.H264x264 or VideoCodec.H265x265 or VideoCodec.H264Qsv or VideoCodec.HevcQsv or VideoCodec.H264Amf;

    /// <summary>Banderas de entrada para decode acelerado por hardware.</summary>
    public static IEnumerable<string> HwAccelInput(HwAccel accel) => accel switch
    {
        // Solo "-hwaccel <api>", SIN "-hwaccel_output_format <api>": el grafo de filtros del motor (split,
        // scale, format, blackdetect, drawtext…) es por SOFTWARE y no puede consumir frames en superficie
        // CUDA/QSV. Forzar el output_format hacía ABORTAR a FFmpeg en cualquier fuente que DECODIFIQUE
        // (File/DeckLink/dshow) con el perfil Nvenc por defecto. Sin él, FFmpeg descarga los frames a CPU
        // automáticamente y el decode acelerado se conserva. (Auditoría 24/7, #49.)
        HwAccel.Nvenc or HwAccel.Nvdec => new[] { "-hwaccel", "cuda" },
        HwAccel.QuickSync              => new[] { "-hwaccel", "qsv" },
        HwAccel.Amf                    => new[] { "-hwaccel", "d3d11va" },
        _ => Array.Empty<string>()
    };
}
