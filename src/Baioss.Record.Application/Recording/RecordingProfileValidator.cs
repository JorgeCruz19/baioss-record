using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Recording;

/// <summary>
/// Validaciones PURAS de un <see cref="RecordingProfile"/> antes de grabar: detecta combinaciones que
/// harían fallar a FFmpeg al arrancar o producir un archivo inválido (bitrate/GOP a cero, resolución
/// impar o nula, calidad fuera de rango, audio sin bitrate). Devuelve mensajes legibles; lista vacía =
/// perfil válido. Sin estado ni IO: testeable de forma aislada.
/// </summary>
public static class RecordingProfileValidator
{
    public static IReadOnlyList<string> Validate(RecordingProfile p)
    {
        var errors = new List<string>();

        // Los códecs intra (ProRes/DNxHD/HR) van por -profile, no por bitrate; calidad constante (CRF/CQ)
        // tampoco usa -b:v. En esos casos un bitrate 0 es legítimo.
        bool intra = p.VideoCodec is VideoCodec.ProRes or VideoCodec.DnxHd or VideoCodec.DnxHr;
        bool usesVideoBitrate = !intra && p.RateControl is not RateControlMode.ConstantQuality;

        if (!p.AudioOnly)
        {
            if (usesVideoBitrate && p.VideoBitrate.BitsPerSecond <= 0)
                errors.Add("El bitrate de vídeo debe ser mayor que cero.");
            if (p.MaxBitrate is { } mx && mx.BitsPerSecond > 0 && mx.BitsPerSecond < p.VideoBitrate.BitsPerSecond)
                errors.Add("El bitrate máximo no puede ser menor que el bitrate de vídeo.");
            if (p.GopSize < 1)
                errors.Add("El tamaño de GOP debe ser al menos 1.");
            if (p.RateControl is RateControlMode.ConstantQuality && (p.Quality < 0 || p.Quality > 51))
                errors.Add("La calidad (CRF/CQ) debe estar entre 0 y 51.");
            if (p.TargetResolution is { } r)
            {
                if (r.Width <= 0 || r.Height <= 0)
                    errors.Add("La resolución de salida debe ser positiva.");
                else if (r.Width % 2 != 0 || r.Height % 2 != 0)
                    errors.Add("La resolución de salida debe tener anchura y altura PARES (requisito de los códecs 4:2:0).");
            }
        }

        if (p.AudioCodec is not AudioCodec.Pcm && p.AudioBitrate.BitsPerSecond <= 0)
            errors.Add("El bitrate de audio debe ser mayor que cero.");
        if (p.AudioSampleRate <= 0)
            errors.Add("La frecuencia de muestreo de audio debe ser mayor que cero.");

        return errors;
    }

    /// <summary>True si el perfil es válido; si no, <paramref name="errors"/> enumera los problemas.</summary>
    public static bool IsValid(RecordingProfile p, out IReadOnlyList<string> errors)
    {
        errors = Validate(p);
        return errors.Count == 0;
    }
}
