using Baioss.Record.Application.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Recording;

/// <summary>
/// Validaciones PURAS de un <see cref="RecordingProfile"/> antes de grabar: detecta combinaciones que
/// harían fallar a FFmpeg al arrancar o producir un archivo inválido (bitrate/GOP a cero, resolución
/// impar o nula, calidad fuera de rango, audio sin bitrate, un contenedor que no puede llevar el códec de
/// vídeo). Devuelve mensajes legibles en el idioma de la aplicación; lista vacía = perfil válido. Sin
/// estado ni IO: testeable de forma aislada.
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

    /// <summary>Qué hace FFmpeg con una familia en un contenedor que no la admite.</summary>
    private enum Misfit
    {
        None,
        /// <summary>El muxer la rechaza al abrir la salida («codec not currently supported in container», «av1 only supported
        /// in MP4 and AVIF»): la grabación no arranca.</summary>
        Refused,
        /// <summary>FFmpeg escribe el archivo SIN error, pero no se puede leer: el AVI no tiene FourCC para HEVC (escribe 0 y se
        /// relee como rawvideo); TS y PS guardan ProRes, DNxHD/HR y AV1 como datos privados o desconocidos.</summary>
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
        var suggested = Suggested(family);
        string alternatives = string.Join(", ", suggested.Take(suggested.Length - 1)) + " " + Localizer.T("Common_Or") + " " + suggested[^1];
        return Localizer.F(misfit == Misfit.Refused ? "Prof_Err_ContainerRefuses" : "Prof_Err_ContainerUnreadable",
            subject, container, name, alternatives);
    }
}
