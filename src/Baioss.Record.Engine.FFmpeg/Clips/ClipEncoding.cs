using Baioss.Record.Application.Clips;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Engine.FFmpeg.Clips;

/// <summary>Qué codificadores por GPU abren en este equipo (se sondean una vez: estar en <c>-encoders</c> no basta).</summary>
public sealed record ClipEncoderSupport(bool H264Nvenc, bool HevcNvenc, bool Av1Nvenc)
{
    public static readonly ClipEncoderSupport CpuOnly = new(false, false, false);
}

/// <summary>
/// Cómo se recodifica un clip en el modo exacto: el perfil con el que se codifica (el de la grabación, o uno hecho a partir de lo
/// que dice ffprobe del archivo), los argumentos de vídeo y audio y el contenedor. Los argumentos de vídeo salen del mismo
/// <see cref="FfmpegArgumentBuilder.VideoEncoderArgs"/> que graba, para que «como la grabación» lo sea de verdad.
/// </summary>
public sealed record ClipEncoding(RecordingProfile Profile, IReadOnlyList<string> VideoArgs, string? VideoFilter,
    IReadOnlyList<string> AudioArgs, ContainerFormat Container)
{
    /// <summary>Codificador de vídeo de FFmpeg («h264_nvenc», «libx264», «prores_aw»…), o null si es solo audio.</summary>
    public string? VideoEncoder => Profile.AudioOnly ? null : FfmpegCodecMap.VideoEncoder(Profile.VideoCodec);

    /// <summary>Codifica en la GPU: si no abre (sesiones de NVENC agotadas por las grabaciones), se repite en la CPU.</summary>
    public bool UsesGpu => !Profile.AudioOnly && FfmpegCodecMap.IsGpuEncoder(Profile.VideoCodec);

    /// <summary>Familias de códec: lo que «como la grabación» conserva.</summary>
    private enum Family { H264, Hevc, Av1, ProRes, DnxHd, DnxHr, Mpeg2 }

    /// <summary>
    /// La codificación del clip. <paramref name="choice"/>: el codificador pedido. <paramref name="media"/>: lo que dice ffprobe
    /// de la primera pieza. <paramref name="recordingProfile"/>: el perfil del canal (puede haber cambiado desde que se grabó:
    /// solo se usan sus parámetros si sigue siendo de la familia del archivo). <paramref name="forceCpu"/>: sin GPU (el reintento
    /// cuando NVENC no abre).
    /// </summary>
    public static ClipEncoding Resolve(ClipVideoEncoder choice, MediaDescription media, RecordingProfile? recordingProfile,
        ClipEncoderSupport support, bool forceCpu = false)
    {
        if (media.Video is not { } video) return AudioOnly(media);

        var family = choice switch
        {
            ClipVideoEncoder.H264 => Family.H264,
            ClipVideoEncoder.Hevc => Family.Hevc,
            ClipVideoEncoder.ProRes => Family.ProRes,
            _ => FamilyOf(video),
        };
        if (family is Family.Av1 && (forceCpu || !support.Av1Nvenc)) family = Family.H264; // AV1 solo por NVENC (RTX 40)

        var codec = ConcreteCodec(family, video, support, forceCpu);
        // «ProRes 422 HQ» elegido a mano es eso, aunque se grabara en ProRes 4444: ni el preset ni el archivo mandan.
        bool explicitProRes = choice is ClipVideoEncoder.ProRes;
        bool fromRecording = !explicitProRes && recordingProfile is { AudioOnly: false } rp && FamilyOf(rp.VideoCodec) == family;
        bool keepSource = !explicitProRes && FamilyOf(video) == family;
        var p = fromRecording ? recordingProfile!.Clone() : Defaults(family, video);
        p.VideoCodec = codec;
        p.HwAccel = HwAccel.None;
        p.AudioOnly = false;
        p.TargetResolution = null;  // el tamaño y la cadencia del archivo: el clip es un trozo de la grabación
        p.OutputFrameRate = null;
        p.BurnTimecode = false;
        p.Segmentation = null;
        p.ScanType = ScanOf(video);
        if (keepSource) KeepSourceQuality(p, family, video);
        // NVENC no codifica 4:2:2 ni H.264 de 10 bits en las tarjetas de este producto: el preset lo habría hecho en CPU.
        if (FfmpegCodecMap.IsGpuEncoder(codec) && !GpuCanEncode(codec, FfmpegCodecMap.PixelFormatArg(p.PixelFormat) ?? video.PixelFormat))
            p.PixelFormat = PixelFormat.Auto;

        var container = family is Family.H264 or Family.Hevc or Family.Av1 ? ContainerFormat.Mp4 : ContainerFormat.Mov;
        p.Container = container;
        SetAudio(p, media, container);

        var frame = new Resolution(video.Width, video.Height);
        var videoArgs = FfmpegArgumentBuilder.VideoEncoderArgs(p, frame, video.FrameRate).ToList();
        // Sin B-frames en H.264/HEVC: con ellos el MP4 lleva una lista de edición (un desfase de 2 cuadros) que el reproductor de
        // Windows no respeta, y enseña el vídeo dos cuadros tarde respecto al audio (medido con Media Foundation). Sin ellos, el
        // primer cuadro está en 0 en todos los reproductores.
        if (family is Family.H264 or Family.Hevc && !videoArgs.Contains("-bf"))
            videoArgs.AddRange(new[] { "-bf", "0" });
        string? fieldOrder = p.ScanType switch { ScanType.InterlacedTff => "tt", ScanType.InterlacedBff => "bb", _ => null };
        if (fieldOrder is not null) videoArgs.AddRange(new[] { "-field_order", fieldOrder });
        string? filter = p.ScanType switch
        {
            // Marca los cuadros con su orden de campos (como la rama de grabación) para que el codificador los codifique así.
            ScanType.InterlacedTff => "setfield=mode=tff",
            ScanType.InterlacedBff => "setfield=mode=bff",
            _ => null,
        };
        return new ClipEncoding(p, videoArgs, filter, FfmpegArgumentBuilder.AudioEncoderArgs(p, channelCount: false).ToList(), container);
    }

    private static Family FamilyOf(MediaStreamInfo video) => video.Codec switch
    {
        "hevc" => Family.Hevc,
        "av1" => Family.Av1,
        "prores" => Family.ProRes,
        "dnxhd" => (video.Profile ?? "").StartsWith("DNXHR", StringComparison.OrdinalIgnoreCase) ? Family.DnxHr : Family.DnxHd,
        "mpeg2video" => Family.Mpeg2,
        _ => Family.H264, // h264 y cualquier otro: H.264, que lee todo el mundo
    };

    private static Family FamilyOf(VideoCodec codec) => codec switch
    {
        VideoCodec.HevcNvenc or VideoCodec.H265x265 or VideoCodec.HevcQsv => Family.Hevc,
        VideoCodec.Av1Nvenc => Family.Av1,
        VideoCodec.ProRes => Family.ProRes,
        VideoCodec.DnxHd => Family.DnxHd,
        VideoCodec.DnxHr => Family.DnxHr,
        VideoCodec.Mpeg2Video => Family.Mpeg2,
        _ => Family.H264,
    };

    private static VideoCodec ConcreteCodec(Family family, MediaStreamInfo video, ClipEncoderSupport support, bool forceCpu) => family switch
    {
        Family.H264 => !forceCpu && support.H264Nvenc && GpuCanEncode(VideoCodec.H264Nvenc, video.PixelFormat) ? VideoCodec.H264Nvenc : VideoCodec.H264x264,
        Family.Hevc => !forceCpu && support.HevcNvenc && GpuCanEncode(VideoCodec.HevcNvenc, video.PixelFormat) ? VideoCodec.HevcNvenc : VideoCodec.H265x265,
        Family.Av1 => VideoCodec.Av1Nvenc,
        Family.ProRes => VideoCodec.ProRes,
        Family.DnxHd => VideoCodec.DnxHd,
        Family.DnxHr => VideoCodec.DnxHr,
        _ => VideoCodec.Mpeg2Video,
    };

    /// <summary>NVENC (Pascal en adelante): 4:2:0 de 8 bits; HEVC y AV1 también de 10. Ni 4:2:2 ni H.264 de 10 bits.</summary>
    private static bool GpuCanEncode(VideoCodec codec, string? pixelFormat) => pixelFormat switch
    {
        null or "yuv420p" or "yuvj420p" or "nv12" => true,
        "yuv420p10le" or "p010le" => codec is VideoCodec.HevcNvenc or VideoCodec.Av1Nvenc,
        _ => false,
    };

    private static ScanType ScanOf(MediaStreamInfo video) => video.FieldOrder switch
    {
        "tt" or "tb" => ScanType.InterlacedTff,
        "bb" or "bt" => ScanType.InterlacedBff,
        _ => ScanType.Progressive,
    };

    /// <summary>Un perfil para un códec que el preset del canal ya no tiene (o que se eligió): a la tasa del archivo y con un GOP
    /// de dos segundos.</summary>
    private static RecordingProfile Defaults(Family family, MediaStreamInfo video)
    {
        double fps = video.FrameRate?.Value ?? 25;
        return new RecordingProfile
        {
            Name = "clip",
            RateControl = family is Family.H264 or Family.Hevc or Family.Av1 ? RateControlMode.VariableBitrate : RateControlMode.ConstantBitrate,
            VideoBitrate = Bitrate.FromMbps(DefaultMbps(video)),
            GopSize = Math.Max(1, (int)Math.Round(fps * 2)),
            ClosedGop = true,
            EncoderProfile = family is Family.ProRes ? EncoderProfile.ProResHq : EncoderProfile.Auto,
        };
    }

    /// <summary>Sin bitrate en el archivo: ~0,1 bits por píxel y cuadro (20 Mbps en 1080p25), entre 4 y 80 Mbps.</summary>
    private static double DefaultMbps(MediaStreamInfo video)
    {
        double fps = video.FrameRate?.Value ?? 25;
        return Math.Clamp(video.Width * (double)video.Height * fps * 0.1 / 1e6, 4, 80);
    }

    /// <summary>
    /// «Como la grabación»: lo que el archivo dice de sí mismo manda sobre el preset (que puede haber cambiado): su tasa (como VBR
    /// con ese objetivo, para que el clip no pese más ni se vea peor), su perfil de ProRes o DNxHR y su formato de píxel.
    /// </summary>
    private static void KeepSourceQuality(RecordingProfile p, Family family, MediaStreamInfo video)
    {
        if (video.BitRate is { } measured && measured > 0 && family is Family.H264 or Family.Hevc or Family.Av1 or Family.Mpeg2 or Family.DnxHd)
        {
            // Con un suelo: una imagen casi quieta graba a decenas de kbps, y recodificada a esa tasa el clip se emborrona (lo que
            // cambia entre cuadros no llega a cada P). Un cuarto de lo de por defecto: 5 Mbps en 1080p25.
            long bps = family is Family.DnxHd ? measured : Math.Max(measured, (long)(DefaultMbps(video) / 4 * 1e6));
            p.VideoBitrate = new Bitrate(bps);
            p.MaxBitrate = family is Family.Mpeg2 or Family.DnxHd ? null : new Bitrate(bps * 3 / 2);
            if (family is not (Family.Mpeg2 or Family.DnxHd)) p.RateControl = RateControlMode.VariableBitrate;
        }
        p.PixelFormat = video.PixelFormat switch
        {
            "yuv420p" or "yuvj420p" or "nv12" => PixelFormat.Yuv420p,
            "yuv422p" or "yuvj422p" => PixelFormat.Yuv422p,
            "yuv420p10le" or "p010le" => PixelFormat.Yuv420p10le,
            "yuv422p10le" => PixelFormat.Yuv422p10le,
            "yuv444p10le" or "yuv444p12le" or "yuva444p10le" => PixelFormat.Yuv444p10le,
            _ => PixelFormat.Auto,
        };
        if (family is Family.H264 or Family.Hevc && p.PixelFormat is PixelFormat.Yuv420p) p.PixelFormat = PixelFormat.Auto; // nv12 en NVENC
        if (family is Family.ProRes)
            p.EncoderProfile = (video.Profile ?? "").ToUpperInvariant() switch
            {
                "PROXY" => EncoderProfile.ProResProxy,
                "LT" => EncoderProfile.ProResLt,
                "STANDARD" => EncoderProfile.ProResStandard,
                "4444" => EncoderProfile.ProRes4444,
                "XQ" or "4444 XQ" => EncoderProfile.ProRes4444Xq,
                _ => EncoderProfile.ProResHq,
            };
        else if (family is Family.DnxHr)
            p.EncoderProfile = (video.Profile ?? "").ToUpperInvariant() switch
            {
                "DNXHR LB" => EncoderProfile.DnxHrLb,
                "DNXHR SQ" => EncoderProfile.DnxHrSq,
                "DNXHR HQX" => EncoderProfile.DnxHrHqx,
                "DNXHR 444" => EncoderProfile.DnxHr444,
                _ => EncoderProfile.DnxHrHq,
            };
    }

    /// <summary>Audio del clip: AAC en MP4 (a la tasa del archivo, si es AAC) y PCM de 24 bits en MOV; las mismas pistas, canales y
    /// frecuencia de muestreo que la grabación.</summary>
    private static void SetAudio(RecordingProfile p, MediaDescription media, ContainerFormat container)
    {
        var a = media.Audio.FirstOrDefault();
        p.AudioCodec = container is ContainerFormat.Mp4 ? AudioCodec.Aac : AudioCodec.Pcm;
        p.AudioSampleRate = a?.SampleRate is > 0 ? a.SampleRate : 48_000;
        if (a is { Codec: "aac", BitRate: { } abps } && abps > 0) p.AudioBitrate = new Bitrate(abps);
        else if (p.AudioBitrate.BitsPerSecond <= 0 || p.AudioCodec is AudioCodec.Aac && p.AudioBitrate.Kbps < 64)
            p.AudioBitrate = Bitrate.FromKbps(256);
    }

    /// <summary>Una grabación de solo audio: se recodifica en su mismo formato (WAV a PCM, MP3 a MP3) o, si no, a AAC en MP4.</summary>
    private static ClipEncoding AudioOnly(MediaDescription media)
    {
        var a = media.Audio.FirstOrDefault();
        var p = new RecordingProfile { Name = "clip", AudioOnly = true, AudioSampleRate = a?.SampleRate is > 0 ? a.SampleRate : 48_000 };
        bool wav = (media.FormatName ?? "").Contains("wav", StringComparison.OrdinalIgnoreCase);
        bool mp3 = a?.Codec == "mp3" && (media.FormatName ?? "").Contains("mp3", StringComparison.OrdinalIgnoreCase);
        p.Container = wav ? ContainerFormat.Wav : mp3 ? ContainerFormat.Mp3Audio : ContainerFormat.Mp4;
        p.AudioCodec = wav ? AudioCodec.Pcm : mp3 ? AudioCodec.Mp3 : AudioCodec.Aac;
        p.AudioBitrate = a?.BitRate is > 0 ? new Bitrate(a.BitRate.Value) : Bitrate.FromKbps(256);
        var audio = FfmpegArgumentBuilder.AudioEncoderArgs(p, channelCount: false).ToList();
        // El PCM del WAV, en el mismo formato de muestra que la grabación (s16, s24, s32…).
        if (wav && a?.Codec is { } pcm && pcm.StartsWith("pcm_", StringComparison.Ordinal)) audio[1] = pcm;
        return new ClipEncoding(p, Array.Empty<string>(), null, audio, p.Container);
    }
}
