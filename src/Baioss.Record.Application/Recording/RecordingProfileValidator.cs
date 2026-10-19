using System.Globalization;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Application.Recording;

/// <summary>
/// Validaciones PURAS de un <see cref="RecordingProfile"/> antes de grabar: detecta combinaciones que
/// harían fallar a FFmpeg al arrancar, producir un archivo inválido o uno distinto del que pide el perfil (bitrate/GOP a cero,
/// resolución impar o nula, calidad fuera de rango, audio sin bitrate, un contenedor que no puede llevar el códec de
/// vídeo o el de audio, una frecuencia de muestreo que el audio o el contenedor no admiten, una cadencia fuera de rango o que el
/// MXF o MPEG-2 no llevan, un formato de píxel o un perfil intra que el códec no graba, un entrelazado que el codificador no
/// codifica, un tamaño que no abre, canales o bitrate de audio que el codificador no admite). Devuelve mensajes legibles en el
/// idioma de la aplicación; lista vacía = perfil válido. Sin estado ni IO: testeable de forma aislada. Lo que acepta graba lo que
/// pide: lo comprueba PresetCombinationSweepTests con el FFmpeg real.
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
                else if (SizeProblem(p, r) is { } size)
                    errors.Add(size);
            }
            if (FrameRateProblem(p) is { } frameRate)
                errors.Add(frameRate);
            errors.AddRange(VideoFormatProblems(p));
        }

        errors.AddRange(AudioContainerProblems(p));
        if (p.AudioCodec is not AudioCodec.Pcm && p.AudioBitrate.BitsPerSecond <= 0)
            errors.Add(Localizer.T("Prof_Err_AudioBitrate"));
        if (p.AudioSampleRate <= 0)
            errors.Add(Localizer.T("Prof_Err_SampleRate"));
        else if (SampleRateProblem(p) is { } rate)
            errors.Add(rate);
        if (AudioChannelsProblem(p) is { } channels)
            errors.Add(channels);

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
    /// y FFmpeg relee enteros. No todos los que la aceptan: AV1 en AVI o HEVC en PS se leen, pero no son lo que nadie espera.</summary>
    private static ContainerFormat[] Suggested(Family family) => family switch
    {
        Family.Hevc => new[] { ContainerFormat.Mp4, ContainerFormat.Mov, ContainerFormat.Mkv, ContainerFormat.Ts },
        Family.Av1 => new[] { ContainerFormat.Mp4, ContainerFormat.Mkv },
        Family.ProRes => new[] { ContainerFormat.Mov, ContainerFormat.Mxf, ContainerFormat.Mkv },
        _ => new[] { ContainerFormat.Mxf, ContainerFormat.Mov, ContainerFormat.Mkv, ContainerFormat.Avi },
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
    /// libaom (av1_nvenc solo abre en una NVIDIA Ada). Lo que no está aquí se relee bien, también el DNxHD clásico en AVI: va
    /// con el FourCC «AVdn» que le pone FfmpegArgumentBuilder.
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
    private static string OrList<T>(IReadOnlyList<T> items) where T : struct, Enum => OrList(items.Select(i => i.ToString()).ToList());

    /// <summary>«16000, 22050 o 24000». Las listas de números van de menor a mayor: así ninguna acaba en uno que se lea con «o»
    /// (8000, 11025), que en español pediría «u».</summary>
    private static string OrList(IReadOnlyList<string> items) => items.Count == 1
        ? items[0]
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

    // --- Frecuencia de muestreo del audio ---

    /// <summary>
    /// Frecuencias (Hz) que abre el codificador de cada audio, de menor a mayor: las que declara FFmpeg. Con otra no abre
    /// («Specified sample rate 44100 is not supported by the libopus encoder») y la grabación no arranca. Medido el 2026-10-18
    /// con el motor real (<c>FfmpegChannelEngine</c>, el argv del producto, que pasa siempre <c>-ar</c>) y FFmpeg 8.0 y el
    /// incluido (git-2026-06-14), iguales: 4 s de un tono con cada audio en cada contenedor que lo lleva, con vídeo y en solo
    /// audio, de 7350 a 384000 Hz, releídos con ffprobe y decodificados enteros. Las de la lista graban y se leen en todos ellos
    /// (Opus queda siempre a 48 kHz en el archivo) salvo lo de <see cref="ContainerRates"/>. AAC, FDK-AAC, Opus y MP3 ajustan
    /// solos el bitrate a lo que cabe a esa frecuencia (256 kb/s a 8 kHz graban); MP2 no (<see cref="Mp2Kbps"/>).
    /// null = PCM: graba a cualquiera.
    /// </summary>
    private static int[]? EncoderRates(AudioCodec encoded) => encoded switch
    {
        AudioCodec.Aac => new[] { 7350, 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000 },
        AudioCodec.FdkAac => new[] { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000 },
        AudioCodec.Opus => new[] { 8000, 12000, 16000, 24000, 48000 },
        AudioCodec.Mp3 => new[] { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000 },
        AudioCodec.Mp2 => new[] { 16000, 22050, 24000, 32000, 44100, 48000 },
        _ => null,
    };

    /// <summary>Las frecuencias a las que el contenedor lleva ese audio, si no son todas las que abre el codificador: el muxer MXF
    /// solo implementa 48 kHz («only 48khz is implemented»), y el MP4 no lleva MP3 por debajo de 16 kHz («muxing mp3 at 8000hz is
    /// not standard»; el MOV sí). null = todas.</summary>
    private static int[]? ContainerRates(ContainerFormat container, AudioCodec encoded) => (container, encoded) switch
    {
        (ContainerFormat.Mxf, _) => new[] { 48000 },
        (ContainerFormat.Mp4, AudioCodec.Mp3) => new[] { 16000, 22050, 24000, 32000, 44100, 48000 },
        _ => null,
    };

    /// <summary>kb/s que abre el codificador MP2 a esa frecuencia: los de MPEG-1 capa II a 32, 44,1 y 48 kHz y los de MPEG-2 (LSF)
    /// a 16, 22,05 y 24 kHz. FFmpeg busca los kb/s enteros (bit_rate / 1000) en la tabla y con otro no abre («bitrate 256 is not
    /// allowed in mp2»), también en mono.</summary>
    private static int[] Mp2Kbps(int rate) => rate is 16000 or 22050 or 24000
        ? new[] { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }
        : new[] { 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 };

    /// <summary>El audio que codifica de verdad el motor: el PCM, en MP4, MOV y TS, como AAC
    /// (<c>FfmpegCodecMap.EffectiveAudioEncoder</c>; una prueba lo ata).</summary>
    private static AudioCodec Encoded(AudioCodec codec, ContainerFormat container) =>
        codec is AudioCodec.Pcm && container is ContainerFormat.Mp4 or ContainerFormat.Mov or ContainerFormat.Ts ? AudioCodec.Aac : codec;

    /// <summary>
    /// El mensaje si el audio no se puede grabar a la frecuencia del perfil, o null: el codificador no la abre (Opus a 44,1 kHz o
    /// MP3 a 96 kHz, en cualquier contenedor), el contenedor no la lleva (MXF fuera de 48 kHz, MP3 en MP4 por debajo de 16 kHz, el
    /// PCM de MP4, MOV y TS, que es AAC, fuera de las de AAC) o MP2 no admite ese bitrate a esa frecuencia. El editor no enseña la
    /// frecuencia (siempre 48 kHz), pero llega otra con un preset importado o editado a mano, o con el perfil guardado de un canal.
    /// </summary>
    private static string? SampleRateProblem(RecordingProfile p)
    {
        // Si el contenedor no lleva ese audio, ya lo dice AudioContainerProblems; con el audio que propone cambian las frecuencias.
        if (AudioMisfitOf(p.AudioCodec, p.Container) != Misfit.None) return null;

        var encoded = Encoded(p.AudioCodec, p.Container);
        int rate = p.AudioSampleRate;
        var byCodec = EncoderRates(encoded);
        var byContainer = ContainerRates(p.Container, encoded);
        bool Takes(int r) => (byCodec?.Contains(r) ?? true) && (byContainer?.Contains(r) ?? true);
        if (!Takes(rate))
        {
            // Que el codificador no la abra es cosa del audio elegido; que el contenedor no la lleve (o que ahí el PCM sea AAC), de la
            // combinación.
            bool codecRefuses = byCodec is not null && !byCodec.Contains(rate) && encoded == p.AudioCodec;
            return Localizer.F(codecRefuses ? "Prof_Err_SampleRateCodec" : "Prof_Err_SampleRateContainer",
                p.AudioCodec, Number(rate), p.Container, OrList((byCodec ?? byContainer!).Where(Takes).Select(Number).ToList()));
        }

        if (encoded is AudioCodec.Mp2 && p.AudioBitrate.BitsPerSecond > 0)
        {
            long kbps = p.AudioBitrate.BitsPerSecond / 1000;
            var table = Mp2Kbps(rate);
            if (!table.Any(k => k == kbps))
                return Localizer.F("Prof_Err_AudioBitrateAtRate", p.AudioCodec, Number(rate),
                    kbps.ToString(CultureInfo.InvariantCulture), OrList(table.Select(Number).ToList()));
        }
        return null;
    }

    // --- Formato de píxel, perfil intra, barrido y tamaño, por códec ---

    /// <summary>
    /// Los formatos de píxel que graba cada códec tal como se piden (además de Auto, que es el suyo), o null si los graba todos. Con
    /// otro, el codificador no lo admite y FFmpeg lo cambia sin avisar por el más parecido que sí (medido el 2026-10-18 con el motor
    /// real y FFmpeg 8.0): MPEG-2 a 10 bits sale en 8; ProRes y DNx no tienen 4:2:0 (ni ProRes 8 bits) y salen en 4:2:2; H.264 por
    /// QuickSync solo hace 4:2:0 de 8 bits. Lo de NVENC, HEVC por QuickSync y AMF depende de la tarjeta: lo que no hace degrada (con la
    /// alarma EncoderFallback) al codificador por CPU, que lo graba.
    /// </summary>
    private static PixelFormat[]? PixelFormatsOf(VideoCodec codec) => codec switch
    {
        VideoCodec.Mpeg2Video => new[] { PixelFormat.Yuv420p, PixelFormat.Yuv422p },
        VideoCodec.H264Qsv => new[] { PixelFormat.Yuv420p },
        VideoCodec.ProRes => new[] { PixelFormat.Yuv422p10le, PixelFormat.Yuv444p10le },
        VideoCodec.DnxHd or VideoCodec.DnxHr => new[] { PixelFormat.Yuv422p, PixelFormat.Yuv422p10le, PixelFormat.Yuv444p10le },
        _ => null,
    };

    private static bool IsProResProfile(EncoderProfile e) => e is EncoderProfile.ProResProxy or EncoderProfile.ProResLt
        or EncoderProfile.ProResStandard or EncoderProfile.ProResHq or EncoderProfile.ProRes4444 or EncoderProfile.ProRes4444Xq;

    private static bool IsDnxProfile(EncoderProfile e) => e is EncoderProfile.DnxHrLb or EncoderProfile.DnxHrSq or EncoderProfile.DnxHrHq
        or EncoderProfile.DnxHrHqx or EncoderProfile.DnxHr444;

    /// <summary>El formato de píxel con el que graba cada perfil intra: ProRes 4444/XQ y DNxHR 444 en 4:4:4 de 10 bits, DNxHR HQX en
    /// 4:2:2 de 10 bits, el resto de ProRes en 4:2:2 de 10 bits y DNxHR LB, SQ y HQ en 4:2:2 de 8 (FfmpegArgumentBuilder.ProResFormat y
    /// el codificador dnxhd, que con otro no abre: «pixel format is incompatible with DNxHR LB/SQ/HQ profile»).</summary>
    private static PixelFormat ProfilePixelFormat(EncoderProfile e) => e switch
    {
        EncoderProfile.ProRes4444 or EncoderProfile.ProRes4444Xq or EncoderProfile.DnxHr444 => PixelFormat.Yuv444p10le,
        EncoderProfile.DnxHrHqx => PixelFormat.Yuv422p10le,
        EncoderProfile.DnxHrLb or EncoderProfile.DnxHrSq or EncoderProfile.DnxHrHq => PixelFormat.Yuv422p,
        _ => PixelFormat.Yuv422p10le, // los ProRes 422
    };

    /// <summary>
    /// ¿Codifica entrelazado de verdad, con los dos campos y los cuadros marcados? x264, MPEG-2, ProRes y el DNxHD clásico (también
    /// el DNxHR SQ, HQ o HQX, que el motor graba como su DNxHD). NVENC, QuickSync y AMF, y x265, guardan los dos campos en cuadros
    /// progresivos: H.264 por NVENC los declara progresivos y el reproductor no desentrelaza, con peine en todo lo que se mueve; HEVC
    /// solo lo dice el contenedor (medido el 2026-10-18 con idet y las marcas de los cuadros decodificados).
    /// </summary>
    private static bool Interlaces(RecordingProfile p) => p.VideoCodec switch
    {
        VideoCodec.H264x264 or VideoCodec.Mpeg2Video or VideoCodec.ProRes => true,
        VideoCodec.DnxHd or VideoCodec.DnxHr => DnxInterlaces(p),
        _ => false,
    };

    /// <summary>El DNxHD clásico tiene CID entrelazado a 1920×1080 (8 y 10 bits) y a 1440×1080 (8 bits), y no en 4:4:4; DNxHR no tiene
    /// ninguno, y LB no tiene DNxHD equivalente. Sin tamaño fijo se graba el de la fuente: no se sabe aquí, y se deja.</summary>
    private static bool DnxInterlaces(RecordingProfile p)
    {
        var pixel = p.PixelFormat is PixelFormat.Auto && IsDnxProfile(p.EncoderProfile) ? ProfilePixelFormat(p.EncoderProfile) : p.PixelFormat;
        if (pixel is PixelFormat.Yuv444p10le) return false;
        if (p.VideoCodec is VideoCodec.DnxHr && p.EncoderProfile is EncoderProfile.DnxHrLb) return false;
        bool tenBit = pixel is PixelFormat.Yuv422p10le or PixelFormat.Yuv420p10le;
        return p.TargetResolution is not { } r || (r.Width == 1920 && r.Height == 1080) || (r.Width == 1440 && r.Height == 1080 && !tenBit);
    }

    /// <summary>¿Sale en 4:2:0? Lo que importa al alto de un entrelazado: cada campo lleva la mitad de las líneas de croma.</summary>
    private static bool Is420(RecordingProfile p) =>
        (PixelFormatsOf(p.VideoCodec) is not { } allowed || allowed.Contains(PixelFormat.Yuv420p))
        && p.PixelFormat is PixelFormat.Auto or PixelFormat.Yuv420p or PixelFormat.Yuv420p10le;

    /// <summary>Los mensajes si el formato de píxel, el perfil intra o el barrido no se graban como los pide el perfil.</summary>
    private static IEnumerable<string> VideoFormatProblems(RecordingProfile p)
    {
        if (p.PixelFormat is not PixelFormat.Auto && PixelFormatsOf(p.VideoCodec) is { } allowed && !allowed.Contains(p.PixelFormat))
            yield return Localizer.F("Prof_Err_PixelFormatCodec", p.VideoCodec, p.PixelFormat, OrList(allowed.Prepend(PixelFormat.Auto).ToList()));
        else if (ProfileProblem(p) is { } profile)
            yield return profile;

        if (p.ScanType is not ScanType.Progressive)
        {
            if (!Interlaces(p))
                yield return p.VideoCodec is VideoCodec.DnxHd or VideoCodec.DnxHr
                    ? Localizer.F("Prof_Err_InterlacedDnx", p.VideoCodec)
                    : Localizer.F("Prof_Err_Interlaced", p.VideoCodec, p.ScanType,
                        OrList(new[] { VideoCodec.H264x264, VideoCodec.Mpeg2Video, VideoCodec.ProRes, VideoCodec.DnxHd }));
            else if (Is420(p) && p.TargetResolution is { Height: > 0 } r && r.Height % 4 != 0)
                yield return Localizer.F("Prof_Err_InterlacedHeight", Number(r.Height), Number(r.Height - r.Height % 4), Number(r.Height - r.Height % 4 + 4));
        }
    }

    /// <summary>
    /// El mensaje si el perfil intra no es del códec (uno de ProRes en DNx o al revés, que quedó al cambiarle el códec al preset) o si
    /// contradice el formato de píxel elegido (ProRes 4444 en 4:2:2, DNxHR HQ en 10 bits: FFmpeg no abre el DNxHR y el ProRes saldría en
    /// el del perfil). En los demás códecs el perfil intra no se usa y no se mira.
    /// </summary>
    private static string? ProfileProblem(RecordingProfile p)
    {
        var e = p.EncoderProfile;
        if (p.VideoCodec is VideoCodec.ProRes && IsDnxProfile(e))
            return Localizer.F("Prof_Err_ProfileFamily", e, "DNxHR", p.VideoCodec, "ProRes");
        if (p.VideoCodec is VideoCodec.DnxHd or VideoCodec.DnxHr && IsProResProfile(e))
            return Localizer.F("Prof_Err_ProfileFamily", e, "ProRes", p.VideoCodec, "DNxHR");
        bool own = p.VideoCodec is VideoCodec.ProRes ? IsProResProfile(e) : p.VideoCodec is VideoCodec.DnxHd or VideoCodec.DnxHr && IsDnxProfile(e);
        if (own && p.PixelFormat is not PixelFormat.Auto && ProfilePixelFormat(e) is var wanted && wanted != p.PixelFormat)
            return Localizer.F("Prof_Err_ProfilePixelFormat", e, wanted, p.PixelFormat);
        return null;
    }

    /// <summary>
    /// El mensaje si el códec no abre a ese tamaño, o null. MPEG-2 no admite un ancho ni un alto múltiplo de 4096 («Width or Height
    /// are not allowed to be multiples of 4096»: el DCI 4K, 4096×2160); DNxHR (y un DNxHD sin CID a ese tamaño, que el motor graba en
    /// DNxHR) no baja de 256×120 («Input dimensions too small»). Medido el 2026-10-18 con FFmpeg 8.0 y el incluido.
    /// </summary>
    private static string? SizeProblem(RecordingProfile p, Resolution r)
    {
        if (p.VideoCodec is VideoCodec.Mpeg2Video && (r.Width % 4096 == 0 || r.Height % 4096 == 0))
            return Localizer.F("Prof_Err_SizeMpeg2", $"{r.Width}x{r.Height}");
        if (p.VideoCodec is VideoCodec.DnxHd or VideoCodec.DnxHr && (r.Width < 256 || r.Height < 120))
            return Localizer.F("Prof_Err_SizeMin", p.VideoCodec, $"{r.Width}x{r.Height}", "256x120");
        return null;
    }

    // --- Canales y bitrate del audio ---

    private static int LayoutChannels(AudioLayout layout) => layout switch
    {
        AudioLayout.Mono => 1, AudioLayout.Surround51 => 6, AudioLayout.Surround71 => 8, _ => 2,
    };

    /// <summary>
    /// El mensaje si el audio no puede llevar los canales del perfil, o el bitrate con esos canales, o null. MP2 y MP3 solo codifican
    /// mono y estéreo (lo que declaran): con 5.1 o 7.1 el codificador no abre y la grabación no arranca (medido el 2026-10-18 con el
    /// motor real y FFmpeg 8.0). Opus admite como mucho 256 kb/s por canal («The bit rate 1600000 bps is unsupported. Please choose
    /// a value between 500 and 1536000» con 5.1). Con varias pistas (pares de una fuente multicanal) cada una es estéreo.
    /// </summary>
    private static string? AudioChannelsProblem(RecordingProfile p)
    {
        // Si el contenedor no lleva ese audio, ya lo dice AudioContainerProblems.
        if (AudioMisfitOf(p.AudioCodec, p.Container) != Misfit.None) return null;
        var encoded = Encoded(p.AudioCodec, p.Container);
        if (encoded is AudioCodec.Mp2 or AudioCodec.Mp3 && p.AudioTracks is AudioTrackMode.Single && LayoutChannels(p.AudioLayout) > 2)
            return Localizer.F("Prof_Err_AudioLayout", p.AudioCodec, p.AudioLayout,
                OrList(new[] { AudioLayout.Mono, AudioLayout.Stereo }), OrList(new[] { AudioCodec.Aac, AudioCodec.Pcm }));
        if (encoded is AudioCodec.Opus)
        {
            int perTrack = p.AudioTracks is AudioTrackMode.Single ? LayoutChannels(p.AudioLayout) : 2;
            long maxKbps = 256L * perTrack;
            long kbps = p.AudioBitrate.BitsPerSecond / 1000;
            if (kbps > maxKbps)
                return Localizer.F("Prof_Err_AudioBitrateChannels", p.AudioCodec, kbps.ToString(CultureInfo.InvariantCulture),
                    perTrack.ToString(CultureInfo.InvariantCulture), maxKbps.ToString(CultureInfo.InvariantCulture));
        }
        return null;
    }

    // --- Cadencia ---

    /// <summary>Lo que se admite como cadencia de un preset: de 1 a 120 cuadros por segundo. Una de 25/1001 (un preset 29.97
    /// duplicado al que se le escribió 25 cuando el editor solo dejaba cambiar el numerador) graba un cuadro cada 40 s.</summary>
    private const double MinFps = 1, MaxFps = 120;

    /// <summary>
    /// Las cadencias que codifica MPEG-2: las que declara mpeg2video (las ocho de su tabla y las de su extensión de cadencia). Con
    /// otra, FFmpeg la cambia sin avisar por la más cercana de estas, y el archivo no lleva la del preset (medido el 2026-10-18 con
    /// FFmpeg 8.0: 13 → 12, 26 → 25, 55 → 59.94, 119 → 120).
    /// </summary>
    private static readonly (int N, int D)[] Mpeg2Rates =
    {
        (1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1), (8, 1), (9, 1), (10, 1), (12, 1), (15, 1), (16, 1), (18, 1), (20, 1), (24, 1),
        (25, 1), (30, 1), (32, 1), (36, 1), (40, 1), (45, 1), (48, 1), (50, 1), (60, 1), (72, 1), (75, 1), (80, 1), (90, 1), (96, 1),
        (100, 1), (120, 1), (1000, 1001), (1200, 1001), (1250, 1001), (1500, 1001), (1600, 1001), (1875, 1001), (2000, 1001),
        (2400, 1001), (2500, 1001), (3000, 1001), (3750, 1001), (4000, 1001), (4800, 1001), (5000, 1001), (6000, 1001), (7500, 1001),
        (8000, 1001), (10000, 1001), (12000, 1001), (15000, 1001), (20000, 1001), (24000, 1001), (30000, 1001), (60000, 1001),
    };

    /// <summary>Las del muxer MXF (las de su tabla de «content package rate»): con otra no escribe la cabecera («Unsupported frame
    /// rate 25/1001») y la grabación no arranca.</summary>
    private static readonly (int N, int D)[] MxfRates =
    {
        (24000, 1001), (24, 1), (25, 1), (30000, 1001), (30, 1), (48000, 1001), (48, 1), (50, 1), (60000, 1001), (60, 1),
        (72000, 1001), (72, 1), (75, 1), (90000, 1001), (90, 1), (96000, 1001), (96, 1), (100, 1), (120000, 1001), (120, 1),
    };

    /// <summary>El mensaje si la cadencia del preset no se puede grabar, o null: fuera de <see cref="MinFps"/>…<see cref="MaxFps"/>,
    /// una que el muxer MXF no admite o una que MPEG-2 no codifica. Con la de la fuente (null) no hay nada que mirar aquí.</summary>
    private static string? FrameRateProblem(RecordingProfile p)
    {
        if (p.OutputFrameRate is not { } rate) return null;
        if (rate.Numerator <= 0 || rate.Denominator <= 0 || rate.Value < MinFps - 1e-9 || rate.Value > MaxFps + 1e-9)
            return Localizer.F("Prof_Err_FrameRate", Fps(rate.Numerator, rate.Denominator),
                Number((int)MinFps), Number((int)MaxFps));
        if (p.Container is ContainerFormat.Mxf && !Holds(MxfRates, rate))
            return Localizer.F("Prof_Err_FrameRateContainer", Fps(rate.Numerator, rate.Denominator), p.Container, Nearest(MxfRates, rate));
        if (p.VideoCodec is VideoCodec.Mpeg2Video && !Holds(Mpeg2Rates, rate))
            return Localizer.F("Prof_Err_FrameRateCodec", "MPEG-2", Fps(rate.Numerator, rate.Denominator), Nearest(Mpeg2Rates, rate));
        return null;
    }

    /// <summary>¿Está <paramref name="rate"/> en la lista? Por el valor de la fracción (50/1 = 100/2).</summary>
    private static bool Holds((int N, int D)[] rates, FrameRate rate) =>
        rates.Any(r => (long)r.N * rate.Denominator == (long)rate.Numerator * r.D);

    /// <summary>Las dos de la lista más cercanas a <paramref name="rate"/>, de menor a mayor («24 o 25»), entre las que se
    /// escriben fácil: las enteras y las NTSC de siempre (23.976, 29.97, 47.952, 59.94, 119.88), no 12000/1001.</summary>
    private static string Nearest((int N, int D)[] rates, FrameRate rate) =>
        OrList(rates.Where(r => r.N >= r.D * MinFps && r.N <= r.D * MaxFps)
            .Where(r => r.D == 1 || (r.D == 1001 && r.N is 24000 or 30000 or 48000 or 60000 or 120000))
            .OrderBy(r => Math.Abs((double)r.N / r.D - rate.Value)).Take(2)
            .OrderBy(r => (double)r.N / r.D).Select(r => Fps(r.N, r.D)).ToList());

    /// <summary>Una cadencia como la escribe el operador en el editor («29.97», «25», «7500/1001»).</summary>
    private static string Fps(int numerator, int denominator) => PresetFrameRate.Format(numerator, denominator);

    /// <summary>«44100», sin separador de miles: como lo enseña el gestor de presets.</summary>
    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
}
