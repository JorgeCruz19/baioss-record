using Baioss.Record.Application.Channels;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Transición de una alarma de análisis de señal: el tipo y si pasa a activa o se despeja.</summary>
public readonly record struct DetectTransition(AlarmType Type, bool Active);

/// <summary>
/// Interpreta las líneas que los filtros de análisis de FFmpeg emiten por <em>stderr</em>:
/// <c>blackdetect</c> (<c>black_start</c>/<c>black_end</c>), <c>freezedetect</c>
/// (<c>freeze_start</c>/<c>freeze_end</c>) y <c>silencedetect</c> (<c>silence_start</c>/<c>silence_end</c>).
/// Puro y testeable: cada línea de marca produce una transición activa/despejada.
/// </summary>
public static class FfmpegDetectParser
{
    /// <summary>Devuelve la transición de alarma si la línea es una marca de detección; si no, <c>null</c>.</summary>
    public static DetectTransition? Parse(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        // Se comprueba primero el cierre (_end) porque "_start"/"_end" son sufijos distintos del mismo
        // prefijo; el orden evita ambigüedad si un build cambiara el formato de la línea.
        if (line.Contains("black_end", StringComparison.Ordinal))   return new(AlarmType.VideoBlack, false);
        if (line.Contains("black_start", StringComparison.Ordinal)) return new(AlarmType.VideoBlack, true);

        if (line.Contains("freeze_end", StringComparison.Ordinal))   return new(AlarmType.VideoFreeze, false);
        if (line.Contains("freeze_start", StringComparison.Ordinal)) return new(AlarmType.VideoFreeze, true);

        if (line.Contains("silence_end", StringComparison.Ordinal))   return new(AlarmType.AudioSilence, false);
        if (line.Contains("silence_start", StringComparison.Ordinal)) return new(AlarmType.AudioSilence, true);

        return null;
    }
}
