using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Lo que el demuxer decklink de FFmpeg cuenta al ABRIR la tarjeta, sacado de su stderr:
/// <list type="bullet">
///   <item>«Found Decklink mode 1920 x 1080 with rate 29.97(i)» (nivel info): el modo con el que abre. Sin
///   <c>-format_code</c> es el que la tarjeta DETECTÓ en la señal (autodetección: FFmpeg espera hasta 3 s a que la
///   tarjeta lo diga); con <c>-format_code</c> es el pedido, se corresponda o no con la señal.</item>
///   <item>«Cannot Autodetect input stream or No signal» (error): en autodetección la tarjeta no dijo nada en esos 3 s
///   —sin señal, dispositivo en uso por otro proceso, o tarjeta/conector sin detección de formato— y FFmpeg sale.</item>
///   <item>«Cannot enable video input» (error): con modo fijo, la tarjeta está en uso por otro proceso (medido en una
///   Duo 2 con la aplicación capturando: en autodetección ese mismo caso sale como el fallo genérico de arriba).</item>
///   <item>«Input #0, decklink, from 'DeckLink Duo (1)':» (info): el dispositivo ABRIÓ y hay captura, en ambos modos.</item>
/// </list>
/// El motor se lo pasa a la fuente (<see cref="ICaptureSource.ReportDetectedMode"/>) y el gestor de entradas lo usa
/// para «Detectar señal». Puro: solo texto.
/// </summary>
public static partial class DecklinkModeParser
{
    public const string AutodetectFailure = "Cannot Autodetect input stream or No signal";

    /// <summary>True si la línea es el fallo de autodetección de FFmpeg.</summary>
    public static bool IsAutodetectFailure(string line) => line.Contains(AutodetectFailure, StringComparison.Ordinal);

    public const string DeviceBusy = "Cannot enable video input";

    /// <summary>True si FFmpeg no pudo encender la entrada de vídeo: la tarjeta está en uso por otro proceso.</summary>
    public static bool IsDeviceBusy(string line) => line.Contains(DeviceBusy, StringComparison.Ordinal);

    /// <summary>True si FFmpeg abrió el dispositivo decklink («Input #0, decklink, from '…':»): a partir de aquí captura.</summary>
    public static bool IsInputOpened(string line) => line.Contains(", decklink, from ", StringComparison.Ordinal);

    /// <summary>
    /// Qué pasó en «Detectar señal» a partir de la salida del intento en autodetección y, si este no dio modo, de un
    /// segundo intento con un modo FIJO: la tarjeta ocupada dice «Cannot enable video input»; libre, abre («Input #0,
    /// decklink…») aunque la señal no coincida con el modo, y entonces es que no hay señal o la tarjeta no detecta.
    /// </summary>
    public static VideoModeDetection Classify(string autodetectOutput, string? fixedModeOutput)
    {
        if (ParseOutput(autodetectOutput) is { } mode) return new VideoModeDetection(VideoModeDetectionOutcome.Detected, mode);
        if (string.IsNullOrEmpty(fixedModeOutput)) return VideoModeDetection.Failed;
        var lines = fixedModeOutput.Split('\n');
        if (lines.Any(IsDeviceBusy)) return new VideoModeDetection(VideoModeDetectionOutcome.DeviceBusy, null);
        if (lines.Any(IsInputOpened)) return new VideoModeDetection(VideoModeDetectionOutcome.NoSignal, null);
        return VideoModeDetection.Failed;
    }

    /// <summary>El modo de una línea «Found Decklink mode W x H with rate R[(i)]», o null si la línea es otra cosa.</summary>
    public static DetectedVideoMode? TryParseFoundMode(string line)
    {
        var m = FoundModeRegex().Match(line);
        if (!m.Success) return null;
        var res = new Resolution(int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture),
                                 int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture));
        var rate = VideoModes.RateFromDisplay(double.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture));
        return new DetectedVideoMode(res, rate, m.Groups["i"].Success);
    }

    /// <summary>Sobre la salida completa de un ffmpeg de sondeo: el modo si lo encontró, null si no.</summary>
    public static DetectedVideoMode? ParseOutput(string output)
        => output.Split('\n').Select(l => TryParseFoundMode(l.TrimEnd('\r'))).FirstOrDefault(m => m is not null);

    // "[decklink @ 000001f4] Found Decklink mode 1920 x 1080 with rate 29.97(i)"  →  1920×1080, 29.97, entrelazado
    [GeneratedRegex(@"Found Decklink mode (?<w>\d+) x (?<h>\d+) with rate (?<r>\d+(?:\.\d+)?)(?<i>\(i\))?")]
    private static partial Regex FoundModeRegex();
}
