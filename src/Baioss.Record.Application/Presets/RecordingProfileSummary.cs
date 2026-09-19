using Baioss.Record.Application.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Presets;

/// <summary>
/// Cómo se le CUENTA al operador el perfil con el que grabará un canal: el nombre del preset que eligió y un resumen
/// técnico de una línea. Compartido por el panel de la aplicación, la ventana de configuración, la API y el cliente web,
/// para que todos digan lo mismo. En el idioma de la aplicación.
/// </summary>
public static class RecordingProfileSummary
{
    /// <summary>
    /// Lo que ve el operador en el badge: el nombre del preset aplicado. Si no se conoce —perfil sembrado, o una
    /// instalación anterior a guardar el nombre, donde el canal puede llevar meses con un preset aplicado— se muestra el
    /// RESUMEN técnico, que siempre es verdad. No el <see cref="RecordingProfile.Name"/>: es el nombre interno del perfil
    /// del canal («MP4 (demo)»), que se conserva al aplicar presets y diría «MP4» de un canal que graba en ProRes.
    /// </summary>
    public static string DisplayName(RecordingProfile profile)
        => string.IsNullOrWhiteSpace(profile.PresetName) ? Describe(profile) : profile.PresetName!;

    /// <summary>Resumen de una línea: «H264x264 · 8 Mbps · nativa · Mp4», «H264x264 · CRF 18 · 1920x1080 · Mp4»,
    /// «ProRes · ProResHq · 1920x1080 · Mov» o, sin vídeo, «Solo audio · Aac · Wav».</summary>
    public static string Describe(RecordingProfile profile)
    {
        if (profile.AudioOnly) return Localizer.F("Ch_Profile_AudioOnly", profile.AudioCodec, profile.Container);
        // La «tasa» es lo que de verdad fija la calidad: CRF en calidad constante; el bitrate si lo hay; y en los
        // códecs intra (ProRes, DNxHR), que no llevan bitrate, su perfil — «0 kbps» no le dice nada a nadie.
        string rate = profile.RateControl == RateControlMode.ConstantQuality ? $"CRF {profile.Quality}"
            : profile.VideoBitrate.BitsPerSecond > 0 ? profile.VideoBitrate.ToString()
            : profile.EncoderProfile != EncoderProfile.Auto ? profile.EncoderProfile.ToString()
            : "";
        string resolution = profile.TargetResolution?.ToString() ?? Localizer.T("Ch_NativeResolution");
        var parts = new[] { profile.VideoCodec.ToString(), rate, resolution, profile.Container.ToString() };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}
