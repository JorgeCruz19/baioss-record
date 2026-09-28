using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Líneas GENÉRICAS del stderr de FFmpeg que valen para cualquier fuente (las específicas de DeckLink están en
/// <see cref="DecklinkModeParser"/>):
/// <list type="bullet">
///   <item>«Input #0, mpegts, from 'srt://0.0.0.0:9000':» — FFmpeg ABRIÓ esa entrada: con una fuente de red, el
///   emisor ya está conectado y hay captura. Se compara la URL con la de la fuente para no confundirla con las
///   entradas generadas (lavfi) de la carta de ajuste.</item>
///   <item>«  Stream #0:0: Video: h264 (Main), yuv420p(progressive), 1920x1080 [SAR 1:1 DAR 16:9], 25 fps, …» — el
///   formato real del vídeo, para fuentes que no lo saben hasta conectar (red, DirectShow).</item>
/// </list>
/// </summary>
public static partial class FfmpegLogLines
{
    /// <summary>True si la línea anuncia que FFmpeg abrió la entrada cuyo <c>-i</c> es <paramref name="inputUri"/>.</summary>
    public static bool IsInputOpened(string line, string? inputUri)
        => !string.IsNullOrEmpty(inputUri) && string.Equals(InputUrl(line), inputUri, StringComparison.Ordinal);

    /// <summary>La URL o dispositivo del <c>-i</c> que anuncia una línea «Input #N, formato, from '…':», o null si la
    /// línea es otra cosa (<see cref="FfmpegInputDump"/> la usa para seguir el volcado de esa entrada).</summary>
    public static string? InputUrl(string line)
    {
        if (!line.StartsWith("Input #", StringComparison.Ordinal)) return null;
        var m = InputOpenedRegex().Match(line);
        return m.Success ? m.Groups["url"].Value : null;
    }

    /// <summary>
    /// El modo de vídeo de una línea «Stream #N:M: Video: …, WxH …, R fps», o null si la línea es otra cosa. La tasa
    /// sale de «fps» y, si no la hay, de «tbr»; el barrido, de «top first»/«bottom first»/«interlaced» en la línea.
    /// </summary>
    public static DetectedVideoMode? TryParseVideoStream(string line)
    {
        if (!line.Contains(": Video: ", StringComparison.Ordinal)) return null;
        var size = SizeRegex().Match(line);
        if (!size.Success) return null;
        var rate = FpsRegex().Match(line);
        if (!rate.Success) rate = TbrRegex().Match(line);
        if (!rate.Success) return null;
        if (!double.TryParse(rate.Groups["r"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || fps <= 0) return null;
        bool interlaced = line.Contains("top first", StringComparison.Ordinal) || line.Contains("bottom first", StringComparison.Ordinal)
                          || line.Contains("interlaced", StringComparison.OrdinalIgnoreCase);
        var res = new Resolution(int.Parse(size.Groups["w"].Value, CultureInfo.InvariantCulture),
                                 int.Parse(size.Groups["h"].Value, CultureInfo.InvariantCulture));
        return new DetectedVideoMode(res, VideoModes.RateFromDisplay(fps), interlaced);
    }

    // "Input #0, mpegts, from 'srt://127.0.0.1:9710':"
    [GeneratedRegex(@"^Input #\d+, [^,]+, from '(?<url>[^']*)'")]
    private static partial Regex InputOpenedRegex();

    // ", 1920x1080 [SAR 1:1 DAR 16:9]," / ", 640x360," — la primera «WxH» delimitada por coma/espacio de la línea de vídeo.
    [GeneratedRegex(@"[ ,](?<w>\d{2,5})x(?<h>\d{2,5})(?=[ ,\[])")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(?<r>\d+(?:\.\d+)?) fps")]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"(?<r>\d+(?:\.\d+)?) tbr")]
    private static partial Regex TbrRegex();
}
