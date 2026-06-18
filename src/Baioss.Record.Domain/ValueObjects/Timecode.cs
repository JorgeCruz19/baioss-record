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

    public char FrameSeparator => DropFrame ? ';' : ':';

    public override string ToString() =>
        $"{Hours:00}:{Minutes:00}:{Seconds:00}{FrameSeparator}{Frames:00}";
}
