using System.Globalization;
using Baioss.Record.Application.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Application.Channels;

/// <summary>
/// La conversión de cadencia que un perfil le pide a FFmpeg sobre una fuente: la salida de grabación lleva <c>-r</c> con la
/// tasa del preset y, si la fuente llega a otra, FFmpeg DESCARTA frames (preset más lento que la fuente) o los REPITE (más
/// rápido) para cuadrar. Es deliberado —lo pide el preset—, así que esos descartes no son «el equipo no da abasto»; pero
/// tampoco hay que callárselo al operador: una fuente a 59,94 grabada a 29,97 pierde la mitad de los frames, y una a 29,97
/// grabada a 59,94 graba cada frame dos veces. Pura y testeable.
/// </summary>
public readonly record struct FrameRateConversion
{
    // Dos tasas que difieren menos que esto son la misma señal: 30000/1001 y «29.97» (2997/100) difieren en una millonésima.
    // 30 frente a 29,97 es un 0,1 %: eso SÍ es una conversión (FFmpeg descarta un frame de cada 1001).
    private const double SameRateTolerance = 0.0005;

    private FrameRateConversion(FrameRate? source, FrameRate? output, bool byFields)
    {
        Source = source;
        Output = output;
        ByFields = byFields;
    }

    /// <summary>Tasa a la que llega la fuente; null si no se conoce.</summary>
    public FrameRate? Source { get; }

    /// <summary>Tasa que fija el perfil (<c>-r</c>); null = la de la fuente, sin conversión.</summary>
    public FrameRate? Output { get; }

    /// <summary>
    /// La conversión es de campos a cuadros o al revés, sin descartar ni repetir nada: un entrelazado lleva dos campos por cuadro, y el
    /// motor saca un cuadro por campo de un 1080i29.97 para un preset 1080p59.94 (bwdif) y teje dos cuadros de un 1080p59.94 en uno de
    /// un preset 1080i29.97 (interlace). Antes la etiqueta decía que se repetía o se descartaba la mitad de los frames.
    /// </summary>
    public bool ByFields { get; }

    /// <param name="source">Tasa a la que llega la fuente (la de su señal), o null si no se conoce.</param>
    /// <param name="output">Tasa de salida del perfil (<c>RecordingProfile.OutputFrameRate</c>), o null si sigue a la fuente.</param>
    /// <param name="sourceScan">Barrido de la señal, si se conoce.</param>
    /// <param name="outputScan">Barrido del perfil.</param>
    public static FrameRateConversion For(FrameRate? source, FrameRate? output, ScanType? sourceScan = null, ScanType outputScan = ScanType.Progressive)
    {
        var s = Valid(source);
        var o = Valid(output);
        // La misma tolerancia que FfmpegArgumentBuilder.DeinterlaceFilter y WeaveFilter, que deciden si se hace.
        static bool Twice(double twice, double once) => Math.Abs(twice - 2 * once) < 0.01 * twice;
        bool byFields = s is { } sr && o is { } or
            && ((sourceScan is ScanType.InterlacedTff or ScanType.InterlacedBff && outputScan is ScanType.Progressive && Twice(or.Value, sr.Value))
                || (sourceScan is ScanType.Progressive && outputScan is not ScanType.Progressive && Twice(sr.Value, or.Value)));
        return new(s, o, byFields);
    }

    private static FrameRate? Valid(FrameRate? rate) => rate is { Numerator: > 0, Denominator: > 0 } r ? r : null;

    /// <summary>El perfil fija una tasa distinta de la de la fuente (las dos conocidas), y no es pasar de campos a cuadros o al revés.</summary>
    public bool IsMismatch => !ByFields && Source is { } s && Output is { } o && Math.Abs(o.Value - s.Value) > SameRateTolerance * s.Value;

    /// <summary>El preset graba MÁS LENTO que la fuente: FFmpeg descarta frames de entrada para convertir.</summary>
    public bool DropsFrames => IsMismatch && Output!.Value.Value < Source!.Value.Value;

    /// <summary>El preset graba MÁS RÁPIDO que la fuente: FFmpeg repite frames para rellenar la cadencia.</summary>
    public bool DuplicatesFrames => IsMismatch && Output!.Value.Value > Source!.Value.Value;

    /// <summary>
    /// El perfil fija una tasa pero no se sabe a cuál llega la fuente: no hay forma de saber qué parte de los descartes de
    /// FFmpeg pide la conversión y qué parte es pérdida.
    /// </summary>
    public bool IsUnknown => Output is not null && Source is null;

    /// <summary>Fracción de los frames de ENTRADA que FFmpeg descarta para convertir: 1 − salida/fuente (½ de 59,94 a 29,97;
    /// ⅙ de 30 a 25). 0 si el preset no baja la tasa.</summary>
    public double DroppedFraction => DropsFrames ? 1 - Output!.Value.Value / Source!.Value.Value : 0;

    /// <summary>Fracción de los frames GRABADOS que son copias: 1 − fuente/salida (½ de 29,97 a 59,94). 0 si el preset no sube
    /// la tasa.</summary>
    public double DuplicatedFraction => DuplicatesFrames ? 1 - Source!.Value.Value / Output!.Value.Value : 0;

    /// <summary>
    /// Aviso para el operador, en el idioma de la aplicación, o null si no hay conversión: «La fuente llega a 59,94 fps y el
    /// preset graba a 29,97 fps: FFmpeg descartará la mitad de los frames para convertir la cadencia…».
    /// </summary>
    public string? Describe()
    {
        if (!IsMismatch) return null;
        string source = Fps(Source!.Value), output = Fps(Output!.Value);
        return DropsFrames
            ? Localizer.F("Cadence_Drops", source, output, Share(DroppedFraction, recorded: false))
            : Localizer.F("Cadence_Duplicates", source, output, Share(DuplicatedFraction, recorded: true));
    }

    /// <summary>Versión corta para el badge del canal («59,94 → 29,97 fps»), o null si no hay conversión.</summary>
    public string? Badge() => IsMismatch ? Localizer.F("Cadence_Badge", Fps(Source!.Value), Fps(Output!.Value)) : null;

    /// <summary>Tasa legible con la cultura del equipo, como el resto de cifras de la aplicación: 25, 29,97, 59,94 (o 29.97
    /// donde el separador decimal es el punto).</summary>
    public static string Fps(FrameRate rate) => rate.Value.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>«la mitad», «1 de cada 6» o «el 58 %» de los frames (grabados). La forma «1 de cada N» solo cuando N sale casi
    /// entero (de 29,97 a 25 son 6,03) o es grande (de 30 a 29,97, 1 de cada 1001); si no, el porcentaje.</summary>
    private static string Share(double fraction, bool recorded)
    {
        double every = 1 / fraction;
        int n = (int)Math.Round(every);
        if (n == 2 && Math.Abs(every - 2) <= 0.04)
            return Localizer.T(recorded ? "Cadence_HalfRecorded" : "Cadence_Half");
        if (n >= 2 && (every >= 10 || Math.Abs(every - n) <= 0.02 * every))
            return Localizer.F(recorded ? "Cadence_OneInRecorded" : "Cadence_OneIn", n);
        int percent = Math.Max(1, (int)Math.Round(fraction * 100));
        return Localizer.F(recorded ? "Cadence_PercentRecorded" : "Cadence_Percent", percent);
    }
}
