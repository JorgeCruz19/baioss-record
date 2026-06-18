namespace Baioss.Record.Domain.ValueObjects;

/// <summary>Resolución de imagen en píxeles. Inmutable.</summary>
public readonly record struct Resolution(int Width, int Height)
{
    public static readonly Resolution Hd720 = new(1280, 720);
    public static readonly Resolution Hd1080 = new(1920, 1080);
    public static readonly Resolution Uhd4K = new(3840, 2160);
    public static readonly Resolution Uhd8K = new(7680, 4320);

    public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// Frecuencia de cuadro como fracción exacta (numerator/denominator) para
/// preservar tasas broadcast como 30000/1001 (29.97) sin error de coma flotante.
/// </summary>
public readonly record struct FrameRate(int Numerator, int Denominator)
{
    public static readonly FrameRate P24 = new(24, 1);
    public static readonly FrameRate P25 = new(25, 1);
    public static readonly FrameRate P2997 = new(30000, 1001);
    public static readonly FrameRate P30 = new(30, 1);
    public static readonly FrameRate P50 = new(50, 1);
    public static readonly FrameRate P5994 = new(60000, 1001);
    public static readonly FrameRate P60 = new(60, 1);

    public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <summary>True para tasas fraccionarias NTSC (29.97/59.94) candidatas a drop-frame.</summary>
    public bool IsDropFrameCandidate => Denominator == 1001;

    public override string ToString() => $"{Value:0.###} fps";
}

/// <summary>Tasa de bits en bits por segundo. Inmutable.</summary>
public readonly record struct Bitrate(long BitsPerSecond)
{
    public static Bitrate FromMbps(double mbps) => new((long)(mbps * 1_000_000));
    public static Bitrate FromKbps(double kbps) => new((long)(kbps * 1_000));

    public double Mbps => BitsPerSecond / 1_000_000d;
    public double Kbps => BitsPerSecond / 1_000d;

    public override string ToString() => Mbps >= 1 ? $"{Mbps:0.##} Mbps" : $"{Kbps:0} kbps";
}
