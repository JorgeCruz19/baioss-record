using Baioss.Record.Application.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Recording;

/// <summary>
/// Validaciones PURAS de un <see cref="RecordingProfile"/> antes de grabar: detecta combinaciones que
/// harían fallar a FFmpeg al arrancar o producir un archivo inválido (bitrate/GOP a cero, resolución
/// impar o nula, calidad fuera de rango, audio sin bitrate, un contenedor que no puede llevar el códec de
/// vídeo o el de audio). Devuelve mensajes legibles en el idioma de la aplicación; lista vacía = perfil válido.
/// Sin estado ni IO: testeable de forma aislada.
/// </summary>
public static class RecordingProfileValidator
{
    public static IReadOnlyList<string> Validate(RecordingProfile p)
    {
        var errors = new List<string>();

        // Los códecs intra van por -profile (ProRes/DNxHR) o por un CID fijo (DNxHD: sin bitrate, el motor toma el de más
        // calidad del formato), no por un bitrate libre; calidad constante (CRF/CQ) tampoco usa -b:v. Ahí un bitrate 0 es legítimo.
        bool intra = p.VideoCodec is VideoCodec.ProRes or VideoCodec.DnxHd or VideoCodec.DnxHr;
        bool usesVideoBitrate = !intra && p.RateControl is not RateControlMode.ConstantQuality;

        if (!p.AudioOnly)
        {
            if (ContainerProblem(p.Container, p.VideoCodec) is { } container)
                errors.Add(container);
            if (usesVideoBitrate && p.VideoBitrate.BitsPerSecond <= 0)
                errors.Add(Localizer.T("Prof_Err_VideoBitrate"));
            if (p.MaxBitrate is { } mx && mx.BitsPerSecond > 0 && mx.BitsPerSecond < p.VideoBitrate.BitsPerSecond)
                errors.Add(Localizer.T("Prof_Err_MaxBitrate"));
            if (p.GopSize < 1)
                errors.Add(Localizer.T("Prof_Err_Gop"));
            if (p.RateControl is RateControlMode.ConstantQuality && (p.Quality < 0 || p.Quality > 51))
                errors.Add(Localizer.T("Prof_Err_Quality"));
            if (p.TargetResolution is { } r)
            {
                if (r.Width <= 0 || r.Height <= 0)
                    errors.Add(Localizer.T("Prof_Err_ResolutionPositive"));
                else if (r.Width % 2 != 0 || r.Height % 2 != 0)
                    errors.Add(Localizer.T("Prof_Err_ResolutionEven"));
            }
        }

        errors.AddRange(AudioContainerProblems(p));
        if (p.AudioCodec is not AudioCodec.Pcm && p.AudioBitrate.BitsPerSecond <= 0)
            errors.Add(Localizer.T("Prof_Err_AudioBitrate"));
        if (p.AudioSampleRate <= 0)
            errors.Add(Localizer.T("Prof_Err_SampleRate"));

        return errors;
    }

    /// <summary>True si el perfil es válido; si no, <paramref name="errors"/> enumera los problemas.</summary>
    public static bool IsValid(RecordingProfile p, out IReadOnlyList<string> errors)
    {
        errors = Validate(p);
        return errors.Count == 0;
    }

    // --- Contenedor × códec de vídeo ---

    /// <summary>Familia de bitstream del códec: el muxer decide por el bitstream, no por el codificador (x265, QuickSync y
    /// NVENC dieron lo mismo en cada contenedor).</summary>
    private enum Family { Hevc, Av1, ProRes, Dnx }

    private static Family? FamilyOf(VideoCodec codec) => codec switch
    {
        VideoCodec.H265x265 or VideoCodec.HevcQsv or VideoCodec.HevcNvenc => Family.Hevc,
        VideoCodec.Av1Nvenc => Family.Av1,
        VideoCodec.ProRes => Family.ProRes,
        VideoCodec.DnxHd or VideoCodec.DnxHr => Family.Dnx,
        _ => null, // H.264 (CPU, NVENC, QuickSync, AMF) y MPEG-2: los lee de vuelta FFmpeg en todos los contenedores de vídeo
    };

    private static string FamilyName(Family family) => family switch
    {
        Family.Hevc => "HEVC",
        Family.Av1 => "AV1",
        Family.ProRes => "ProRes",
        _ => "DNxHD/DNxHR",
    };

    /// <summary>Contenedores que se le proponen a cada familia en el mensaje: los que la llevan con su identificador estándar
    /// y FFmpeg relee enteros. No todos los que la aceptan: AV1 en AVI o HEVC en PS se leen, pero no son lo que nadie espera, y
    /// DNx en AVI solo se lee como DNxHR (ver <see cref="MisfitOf"/>).</summary>
    private static ContainerFormat[] Suggested(Family family) => family switch
    {
        Family.Hevc => new[] { ContainerFormat.Mp4, ContainerFormat.Mov, ContainerFormat.Mkv, ContainerFormat.Ts },
        Family.Av1 => new[] { ContainerFormat.Mp4, ContainerFormat.Mkv },
        Family.ProRes => new[] { ContainerFormat.Mov, ContainerFormat.Mxf, ContainerFormat.Mkv },
        _ => new[] { ContainerFormat.Mxf, ContainerFormat.Mov, ContainerFormat.Mkv },
    };

    /// <summary>Qué hace FFmpeg con un códec (una familia de vídeo o un audio) en un contenedor que no lo admite.</summary>
    private enum Misfit
    {
        None,
        /// <summary>El muxer lo rechaza al abrir la salida («codec not currently supported in container», «av1 only supported
        /// in MP4 and AVIF», «Unsupported audio codec»): la grabación no arranca.</summary>
        Refused,
        /// <summary>FFmpeg escribe el archivo SIN error, pero no se puede leer: el AVI no tiene FourCC para HEVC (escribe 0 y se
        /// relee como rawvideo); TS y PS guardan ProRes, DNxHD/HR y AV1 como datos privados o desconocidos; el WAV no delimita
        /// los paquetes del AAC.</summary>
        Unreadable,
    }

    /// <summary>
    /// Medido el 2026-10-18 con FFmpeg 8.0 y con el incluido (git-2026-06-14), iguales: 2 s codificados en cada contenedor de
    /// vídeo con cada códec del producto y releídos con ffprobe y una decodificación entera. AV1 se midió con libsvtav1 y
    /// libaom (av1_nvenc solo abre en una NVIDIA Ada). Lo que no está aquí se relee bien. DNxHD en AVI depende del CID (el
    /// DNxHD clásico sale como rawvideo, el DNxHR no), que se decide con el tamaño de la fuente: no se puede juzgar aquí.
    /// </summary>
    private static Misfit MisfitOf(Family family, ContainerFormat container) => (family, container) switch
    {
        (Family.Hevc, ContainerFormat.Avi) => Misfit.Unreadable,
        (Family.Hevc, ContainerFormat.Mxf) => Misfit.Refused,
        (Family.Av1, ContainerFormat.Mxf or ContainerFormat.Mov) => Misfit.Refused,
        (Family.Av1, ContainerFormat.Ts or ContainerFormat.ProgramStream) => Misfit.Unreadable,
        (Family.ProRes or Family.Dnx, ContainerFormat.Mp4) => Misfit.Refused,
        (Family.ProRes or Family.Dnx, ContainerFormat.Ts or ContainerFormat.ProgramStream) => Misfit.Unreadable,
        _ => Misfit.None,
    };

    /// <summary>
    /// El mensaje si el contenedor no puede llevar el códec de vídeo del perfil, o null. Un contenedor de solo audio (WAV,
    /// MP3) no lleva vídeo de ningún tipo: su muxer rechaza la pista («wav muxer does not support any stream of type video»).
    /// </summary>
    private static string? ContainerProblem(ContainerFormat container, VideoCodec codec)
    {
        if (container is ContainerFormat.Wav or ContainerFormat.Mp3Audio)
            return Localizer.F("Prof_Err_AudioOnlyContainer", container);
        if (FamilyOf(codec) is not { } family) return null;
        var misfit = MisfitOf(family, container);
        if (misfit == Misfit.None) return null;

        string name = FamilyName(family);
        // «HevcQsv (HEVC)»: el nombre que el operador eligió en el editor y la familia que decide; «ProRes», no «ProRes (ProRes)».
        string subject = string.Equals(codec.ToString(), name, StringComparison.OrdinalIgnoreCase) ? name : $"{codec} ({name})";
        return Localizer.F(misfit == Misfit.Refused ? "Prof_Err_ContainerRefuses" : "Prof_Err_ContainerUnreadable",
            subject, container, name, OrList(Suggested(family)));
    }

    /// <summary>«Mp4, Mov, Mkv o Ts»; «Pcm» si es uno solo.</summary>
    private static string OrList<T>(IReadOnlyList<T> items) where T : struct, Enum => items.Count == 1
        ? items[0].ToString()
        : string.Join(", ", items.Take(items.Count - 1)) + " " + Localizer.T("Common_Or") + " " + items[^1];

    // --- Contenedor × códec de audio ---

    /// <summary>
    /// Medido el 2026-10-18 con el motor real (<c>FfmpegChannelEngine</c>, el argv del producto) y FFmpeg 8.0 y el incluido
    /// (git-2026-06-14): 4 s de un tono con cada códec de audio en cada contenedor, con vídeo y en solo audio, releídos con
    /// ffprobe y decodificados enteros. Lo que no está aquí se relee bien: MP4, MKV y TS llevan todos. PCM en MP4, MOV y TS
    /// tampoco falla porque el motor lo graba como AAC (<c>FfmpegCodecMap.EffectiveAudioEncoder</c>); en PS no lo promueve.
    /// </summary>
    private static Misfit AudioMisfitOf(AudioCodec codec, ContainerFormat container) => (codec, container) switch
    {
        // El muxer MXF pasa el audio por pcm_rechunk, que solo admite PCM.
        (not AudioCodec.Pcm, ContainerFormat.Mxf) => Misfit.Refused,
        // El muxer de PS: «Must be one of mp1, mp2, mp3, 16-bit pcm_dvd, pcm_s16be, ac3 or dts»; el PCM del producto es de 24 bits.
        (not (AudioCodec.Mp2 or AudioCodec.Mp3), ContainerFormat.ProgramStream) => Misfit.Refused,
        // El muxer MP3: «Exactly one MP3 audio stream is required».
        (not AudioCodec.Mp3, ContainerFormat.Mp3Audio) => Misfit.Refused,
        // Opus: el muxer MOV lo rechaza («opus only supported in MP4») y los RIFF (AVI, WAV) no tienen etiqueta para él («Codec
        // opus not supported in WAVE format»): no escriben la cabecera.
        (AudioCodec.Opus, ContainerFormat.Avi or ContainerFormat.Mov or ContainerFormat.Wav) => Misfit.Refused,
        // El FDK-AAC del incluido sale en ADTS y los muxers RIFF (AVI, WAV) no lo aceptan («ADTS is only supported with codec
        // tag 0x1610»). FFmpeg 8.0 sí lo graba, pero un preset no puede grabar en un equipo y en otro no.
        (AudioCodec.FdkAac, ContainerFormat.Avi or ContainerFormat.Wav) => Misfit.Refused,
        // El WAV no delimita los paquetes: el AAC del codificador nativo se escribe sin error y no se decodifica (los dos FFmpeg).
        (AudioCodec.Aac, ContainerFormat.Wav) => Misfit.Unreadable,
        _ => Misfit.None,
    };

    /// <summary>Audio que se le propone a cada contenedor en el mensaje: el que lleva con su identificador estándar. No todo el
    /// que acepta: AAC en AVI o MP2 en MOV se leen, pero no son lo que nadie espera, y PCM en MOV se grabaría como AAC.</summary>
    private static AudioCodec[] SuggestedAudio(ContainerFormat container) => container switch
    {
        ContainerFormat.Mxf or ContainerFormat.Wav => new[] { AudioCodec.Pcm },
        ContainerFormat.ProgramStream => new[] { AudioCodec.Mp2, AudioCodec.Mp3 },
        ContainerFormat.Mp3Audio => new[] { AudioCodec.Mp3 },
        ContainerFormat.Avi => new[] { AudioCodec.Pcm, AudioCodec.Mp3, AudioCodec.Mp2 },
        _ => new[] { AudioCodec.Aac }, // MOV; MP4, MKV y TS no rechazan ningún audio
    };

    /// <summary>
    /// Los mensajes si el contenedor no puede llevar el audio del perfil. MXF, además, necesita una pista de vídeo y que sea la
    /// primera («there must be exactly one video stream and it must be the first one»): en solo audio no graba con ningún códec.
    /// </summary>
    private static IEnumerable<string> AudioContainerProblems(RecordingProfile p)
    {
        if (p.AudioOnly && p.Container is ContainerFormat.Mxf)
            yield return Localizer.F("Prof_Err_ContainerNeedsVideo", p.Container);
        var misfit = AudioMisfitOf(p.AudioCodec, p.Container);
        if (misfit != Misfit.None)
            yield return Localizer.F(misfit == Misfit.Refused ? "Prof_Err_AudioContainerRefuses" : "Prof_Err_AudioContainerUnreadable",
                p.AudioCodec, p.Container, OrList(SuggestedAudio(p.Container)));
    }
}
