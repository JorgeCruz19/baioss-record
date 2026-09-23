using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Application.Capture;

/// <summary>
/// Modo de vídeo que la tarjeta DETECTÓ en la señal (DeckLink en autodetección): es lo que FFmpeg cuenta al abrir el
/// dispositivo («Found Decklink mode 1920 x 1080 with rate 29.97(i)»). Distinto de <see cref="DeviceFormat"/>, que es
/// un modo de la LISTA de los que admite la tarjeta: <see cref="VideoModes.FindMatch"/> casa el uno con el otro.
/// </summary>
/// <param name="Resolution">Ancho × alto del modo.</param>
/// <param name="FrameRate">Tasa de CUADRO exacta (30000/1001 para 1080i59.94), no la de campos.</param>
/// <param name="Interlaced">True si la señal es entrelazada.</param>
public sealed record DetectedVideoMode(Resolution Resolution, FrameRate FrameRate, bool Interlaced)
{
    /// <summary>«1920×1080 · 59.94i»: la misma etiqueta que el gestor de entradas da a los modos de la lista.</summary>
    public string Label => VideoModes.Label(Resolution, FrameRate, Interlaced);
}

/// <summary>Qué pasó al preguntarle a la tarjeta por su señal («Detectar señal»).</summary>
public enum VideoModeDetectionOutcome
{
    /// <summary>La tarjeta detectó la señal: <see cref="VideoModeDetection.Mode"/> la describe.</summary>
    Detected,
    /// <summary>La tarjeta está en uso por otro proceso (otro programa, u otro canal con la misma entrada).</summary>
    DeviceBusy,
    /// <summary>La tarjeta está libre y abre, pero no detectó ninguna señal: sin señal o cable, o tarjeta/conector sin detección.</summary>
    NoSignal,
    /// <summary>No se pudo consultar (FFmpeg no arrancó, salida inesperada…).</summary>
    Failed,
}

/// <summary>Resultado de «Detectar señal»: el modo si lo hubo y, si no, por qué.</summary>
public sealed record VideoModeDetection(VideoModeDetectionOutcome Outcome, DetectedVideoMode? Mode)
{
    public static readonly VideoModeDetection Failed = new(VideoModeDetectionOutcome.Failed, null);
}

/// <summary>Etiquetas y cadencias de los modos de vídeo, compartidas por la lista de modos y por el modo detectado.</summary>
public static class VideoModes
{
    /// <summary>«1920×1080 · 59.94i» / «1280×720 · 50p»: en entrelazado la tasa que se enseña es la de CAMPOS (convención
    /// broadcast: 1080i59.94 va a 29.97 cuadros por segundo), en progresivo la de cuadro.</summary>
    public static string Label(Resolution res, FrameRate rate, bool interlaced)
    {
        double display = interlaced ? rate.Value * 2 : rate.Value;
        char scan = interlaced ? 'i' : 'p';
        return $"{res.Width}×{res.Height} · {FormatRate(display)}{scan}";
    }

    /// <summary>"59.94", "29.97", "23.98" o entero exacto ("25", "50", "60").</summary>
    public static string FormatRate(double v)
    {
        double rounded = Math.Round(v);
        return Math.Abs(v - rounded) < 0.02
            ? ((int)rounded).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// La fracción exacta de una tasa impresa con dos decimales, como la escribe FFmpeg («29.97» → 30000/1001, «59.94» →
    /// 60000/1001, «23.98» → 24000/1001, «25.00» → 25/1). Las tasas NTSC son n/1.001; el resto, enteras. Una tasa rara
    /// se conserva a milésimas.
    /// </summary>
    public static FrameRate RateFromDisplay(double shown)
    {
        // Tolerancias por debajo de la centésima con la que imprime FFmpeg: «23.98» está a 0,02 de 24 (y en coma flotante
        // un pelo por debajo), así que un umbral de 0,02 lo tomaba por 24 entero.
        double rounded = Math.Round(shown);
        if (Math.Abs(shown - rounded) < 0.005) return new FrameRate((int)rounded, 1);
        int ntsc = (int)Math.Round(shown * 1.001);
        if (Math.Abs(shown - ntsc / 1.001) < 0.01) return new FrameRate(ntsc * 1000, 1001);
        return new FrameRate((int)Math.Round(shown * 1000), 1000);
    }

    /// <summary>
    /// El modo de la lista de la tarjeta que corresponde a la señal detectada: misma resolución, mismo barrido y misma
    /// cadencia (con la tolerancia de dos decimales con la que FFmpeg la imprime). Null si ninguno casa. El barrido
    /// distingue 1080i59.94 («Hi59», 30000/1001 entrelazado) de 1080p29.97 («Hp29», la misma tasa de cuadro).
    /// </summary>
    public static DeviceFormat? FindMatch(IEnumerable<DeviceFormat> formats, DetectedVideoMode mode)
        => formats.FirstOrDefault(f => f.Resolution == mode.Resolution && f.Interlaced == mode.Interlaced
                                       && f.FrameRate is { } r && Math.Abs(r.Value - mode.FrameRate.Value) < 0.02);
}
