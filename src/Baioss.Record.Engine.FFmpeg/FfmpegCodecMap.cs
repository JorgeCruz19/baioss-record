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
        VideoCodec.ProRes    => "prores_ks",
        VideoCodec.DnxHd     => "dnxhd",
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
    /// Encoder de audio compatible con el contenedor. MP4/MOV/TS no admiten PCM s24le
    /// de forma estándar, así que se promueve a AAC automáticamente.
    /// </summary>
    public static string EffectiveAudioEncoder(AudioCodec codec, ContainerFormat container) =>
        codec == AudioCodec.Pcm && container is ContainerFormat.Mp4 or ContainerFormat.Mov or ContainerFormat.Ts
            ? "aac"
            : AudioEncoder(codec);

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
