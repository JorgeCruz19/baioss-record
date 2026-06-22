using System.Globalization;

namespace Baioss.Record.Domain.ValueObjects;

/// <summary>
/// Timecode SMPTE con conciencia de drop-frame. Inmutable.
/// Formato non-drop: HH:MM:SS:FF — drop-frame: HH:MM:SS;FF
/// </summary>
public readonly record struct Timecode(int Hours, int Minutes, int Seconds, int Frames, bool DropFrame = false)
{
    public static readonly Timecode Zero = new(0, 0, 0, 0);

    /// <summary>Parsea "HH:MM:SS:FF" (non-drop) o "HH:MM:SS;FF" (drop-frame).</summary>
    public static Timecode Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        bool drop = value.Contains(';', StringComparison.Ordinal);
        var parts = value.Split(':', ';');
        if (parts.Length != 4)
            throw new FormatException($"Timecode inválido: '{value}'.");

        return new Timecode(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture),
            drop);
    }

    /// <summary>Convierte un número total de cuadros a timecode dada la tasa nominal entera (24/25/30/50/60).</summary>
    public static Timecode FromFrameNumber(long frameNumber, int nominalRate, bool dropFrame = false)
    {
        if (nominalRate <= 0) throw new ArgumentOutOfRangeException(nameof(nominalRate));
        long frames = frameNumber;
        int ff = (int)(frames % nominalRate);
        long totalSeconds = frames / nominalRate;
        int ss = (int)(totalSeconds % 60);
        int mm = (int)(totalSeconds / 60 % 60);
        int hh = (int)(totalSeconds / 3600);
        return new Timecode(hh, mm, ss, ff, dropFrame);
    }

    /// <summary>
    /// Construye el timecode a partir de la duración REAL de medios (microsegundos, p. ej. el
    /// <c>out_time_us</c> de FFmpeg). A diferencia de <see cref="FromFrameNumber"/>, el campo HH:MM:SS
    /// se deriva del tiempo transcurrido —no de <c>cuadros ÷ tasa</c>—, así que NO se acelera ni "salta"
    /// con fuentes a 50/59.94/60 fps; los cuadros (FF) son solo la subdivisión dentro del segundo según
    /// la tasa nominal. No aplica drop-frame: al venir del tiempo real ya coincide con el reloj de pared.
    /// </summary>
    public static Timecode FromMicroseconds(long microseconds, int nominalRate)
    {
        if (nominalRate <= 0) nominalRate = 25;
        if (microseconds < 0) microseconds = 0;

        long totalSeconds = microseconds / 1_000_000;
        long subUs = microseconds % 1_000_000;
        int ff = (int)(subUs * nominalRate / 1_000_000);
        if (ff >= nominalRate) ff = nominalRate - 1;        // por redondeo en el borde del segundo

        int ss = (int)(totalSeconds % 60);
        int mm = (int)(totalSeconds / 60 % 60);
        int hh = (int)(totalSeconds / 3600);
        return new Timecode(hh, mm, ss, ff);
    }

    public char FrameSeparator => DropFrame ? ';' : ':';

    public override string ToString() =>
        $"{Hours:00}:{Minutes:00}:{Seconds:00}{FrameSeparator}{Frames:00}";
}
