using System.Globalization;

namespace Baioss.Record.Application.Presets;

/// <summary>
/// La cadencia de un preset tal como la escribe el operador en el editor: «25», «29.97» (o «29,97»), «59.94», «23.976» o la fracción
/// «30000/1001»; vacío o 0, la de la fuente. Las de NTSC (k·1000/1001) se guardan exactas: «29.97» es 30000/1001, no 2997/100. Antes
/// el editor solo dejaba escribir un entero, que iba al numerador: un preset nuevo no podía ser 29.97, y en una copia de un preset
/// 29.97 (30000/1001) el campo enseñaba «30000» y escribir 25 dejaba 25/1001, un cuadro cada 40 s.
/// </summary>
public static class PresetFrameRate
{
    /// <summary>Las cadencias NTSC con su nombre de siempre (23.976 y 47.952 con tres decimales, el resto con dos).</summary>
    private static readonly Dictionary<int, string> NtscNames = new()
    {
        [24000] = "23.976", [30000] = "29.97", [48000] = "47.952", [60000] = "59.94", [120000] = "119.88",
    };

    /// <summary>Cómo se enseña la cadencia <paramref name="numerator"/>/<paramref name="denominator"/>: vacío si es la de la fuente
    /// (0), el entero, el nombre NTSC o, si no es ninguna de esas, la fracción tal cual (así se ve una rara, como 25/1001).</summary>
    public static string Format(int numerator, int denominator)
    {
        if (numerator <= 0) return "";
        if (denominator == 1) return numerator.ToString(CultureInfo.InvariantCulture);
        if (denominator == 1001 && NtscNames.TryGetValue(numerator, out var name)) return name;
        double value = denominator > 0 ? (double)numerator / denominator : 0;
        // Una fracción con un decimal corto (12.5 = 25/2) se lee mejor como número.
        if (denominator > 0 && Math.Abs(Math.Round(value, 3) - value) < 1e-9 && denominator != 1001)
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{numerator}/{denominator}");
    }

    /// <summary>
    /// Lee lo que escribió el operador. Vacío o 0: la de la fuente (0/1). Una fracción «n/d» se toma tal cual. Un número a menos de
    /// 0,005 de una NTSC (k·1000/1001, k entero) es esa NTSC exacta: 29.97, 23.98, 59.94; un entero es n/1; cualquier otro, en
    /// milésimas (12.5 = 25/2). False si no es un número, si es negativo o si su fracción no cabe.
    /// </summary>
    public static bool TryParse(string? text, out int numerator, out int denominator)
    {
        numerator = 0;
        denominator = 1;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return true;
        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(s[..slash].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                || !int.TryParse(s[(slash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d <= 0)
                return false;
            if (n == 0) return true;
            numerator = n;
            denominator = d;
            return true;
        }
        if (!double.TryParse(s.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || value < 0 || value > 100_000)
            return false;
        if (value == 0) return true;
        double whole = Math.Round(value);
        if (Math.Abs(value - whole) < 1e-6)
        {
            numerator = (int)whole;
            return true;
        }
        // k·1000/1001 queda a 0,1 % por debajo de k: 29.97 es 30 (k = 30), 23.976 es 24.
        double k = Math.Round(value * 1001 / 1000);
        if (k >= 1 && Math.Abs(value - k * 1000 / 1001) < 0.005)
        {
            numerator = (int)k * 1000;
            denominator = 1001;
            return true;
        }
        int milli = (int)Math.Round(value * 1000);
        int gcd = Gcd(milli, 1000);
        numerator = milli / gcd;
        denominator = 1000 / gcd;
        return numerator > 0;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
