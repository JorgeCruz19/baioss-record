using System.Globalization;

namespace Baioss.Record.Application.Storage;

/// <summary>
/// Formateo legible de tamaños de almacenamiento y duraciones para la UI/API (puro y testeable). Usa
/// <see cref="CultureInfo.InvariantCulture"/> para que la salida sea determinista (punto decimal) con
/// independencia del idioma del equipo. (Gestión de almacenamiento — Fase 4.)
/// </summary>
public static class StorageFormat
{
    private const double KiB = 1024d;
    private const double MiB = 1024d * 1024;
    private const double GiB = 1024d * 1024 * 1024;
    private const double TiB = 1024d * 1024 * 1024 * 1024;

    /// <summary>Bytes → «1.5 GB» / «820 MB» / «512 KB» / «40 B» (base 1024; se etiqueta GB/MB por convención). Negativo → 0 B.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        return bytes >= TiB ? Fmt(bytes / TiB, "0.00", "TB")
             : bytes >= GiB ? Fmt(bytes / GiB, "0.0", "GB")
             : bytes >= MiB ? Fmt(bytes / MiB, "0", "MB")
             : bytes >= KiB ? Fmt(bytes / KiB, "0", "KB")
             : $"{bytes} B";
    }

    private static string Fmt(double value, string numberFormat, string unit)
        => value.ToString(numberFormat, CultureInfo.InvariantCulture) + " " + unit;

    /// <summary>Duración → «1 h 02 min» / «45 min» / «12 s». Negativa → «0 s».</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} h {d.Minutes:00} min";
        if (d.TotalMinutes >= 1) return $"{d.Minutes} min";
        return $"{d.Seconds} s";
    }
}
