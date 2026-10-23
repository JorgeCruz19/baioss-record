using System.Globalization;
using System.Text.RegularExpressions;

namespace Baioss.Record.Application.Clips;

/// <summary>Nombres de los clips: legibles, válidos en Windows y sin pisar nada de lo que ya hay en la carpeta.</summary>
public static partial class ClipNaming
{
    /// <summary>Tope del nombre (sin extensión): con la carpeta, lejos del límite de 260 caracteres de las rutas.</summary>
    public const int MaxLength = 120;

    /// <summary>
    /// El nombre de la grabación: su primer archivo sin extensión y, si está partida en segmentos o piezas («Noticias_1.mp4»,
    /// «Noticias_2.mp4»…), sin el número de segmento.
    /// </summary>
    public static string RecordingName(string? firstFilePath, int files)
    {
        var name = string.IsNullOrWhiteSpace(firstFilePath) ? "" : Path.GetFileNameWithoutExtension(firstFilePath);
        if (files > 1 && SegmentSuffix().Match(name) is { Success: true } m) name = m.Groups["base"].Value;
        return Sanitize(name) ?? "clip";
    }

    /// <summary>«Noticias (00.12.05-00.12.35)»: la grabación y el tramo (posiciones desde su principio, en HH.MM.SS).</summary>
    public static string ClipName(string recordingName, TimeSpan from, TimeSpan to)
    {
        var name = $"{recordingName} ({Position(from)}-{Position(to)})";
        return Sanitize(name) ?? "clip";
    }

    private static string Position(TimeSpan t)
    {
        long s = (long)Math.Floor(t.TotalSeconds + 1e-6);
        return string.Create(CultureInfo.InvariantCulture, $"{s / 3600:00}.{s / 60 % 60:00}.{s % 60:00}");
    }

    /// <summary>Sin caracteres que Windows no admite en un nombre de archivo ni espacios o puntos al final; vacío → null.</summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength];
        cleaned = cleaned.TrimEnd(' ', '.');
        return cleaned.Length == 0 || cleaned.All(c => c == '_') ? null : cleaned;
    }

    /// <summary>La ruta del clip en <paramref name="folder"/>: «nombre.ext» o, si ya existe (o está a medias), «nombre 2.ext»,
    /// «nombre 3.ext»…</summary>
    public static string UniquePath(string folder, string name, string extension, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || File.Exists(p + PartSuffix);
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, (n == 1 ? name : $"{name} {n}") + "." + extension);
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>Sufijo del archivo mientras FFmpeg lo escribe: solo se le pone su nombre cuando está entero.</summary>
    public const string PartSuffix = ".part";

    [GeneratedRegex(@"^(?<base>.*\S)_(?<n>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentSuffix();
}
