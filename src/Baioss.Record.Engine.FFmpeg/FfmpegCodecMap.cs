using Baioss.Record.Domain;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Traduce los enums de dominio a nombres de encoder/muxer de FFmpeg.</summary>
internal static class FfmpegCodecMap
{
    public static string VideoEncoder(VideoCodec codec) => codec switch
    {
        VideoCodec.H264Nvenc => "h264_nvenc",
        VideoCodec.HevcNvenc => "hevc_nvenc",
        VideoCodec.Av1Nvenc  => "av1_nvenc",
        VideoCodec.H264x264  => "libx264",
        VideoCodec.H265x265  => "libx265",
        VideoCodec.ProRes    => "prores_ks",
        VideoCodec.DnxHd     => "dnxhd",
        VideoCodec.DnxHr     => "dnxhd", // DNxHR vía -profile dnxhr_hq
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
    };

    public static string AudioEncoder(AudioCodec codec) => codec switch
    {
        AudioCodec.Pcm    => "pcm_s24le",
        AudioCodec.Aac    => "aac",
        AudioCodec.FdkAac => "libfdk_aac",
        AudioCodec.Opus   => "libopus",
        AudioCodec.Mp3    => "libmp3lame",
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
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
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>True si el encoder corre en GPU (afecta a la cadena de filtros _cuda).</summary>
    public static bool IsGpuEncoder(VideoCodec codec) =>
        codec is VideoCodec.H264Nvenc or VideoCodec.HevcNvenc or VideoCodec.Av1Nvenc;

    /// <summary>Banderas de entrada para decode acelerado por hardware.</summary>
    public static IEnumerable<string> HwAccelInput(HwAccel accel) => accel switch
    {
        HwAccel.Nvenc or HwAccel.Nvdec => new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" },
        HwAccel.QuickSync              => new[] { "-hwaccel", "qsv", "-hwaccel_output_format", "qsv" },
        HwAccel.Amf                    => new[] { "-hwaccel", "d3d11va" },
        _ => Array.Empty<string>()
    };
}
