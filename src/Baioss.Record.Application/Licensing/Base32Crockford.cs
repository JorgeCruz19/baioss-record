using System.Text;

namespace Baioss.Record.Application.Licensing;

/// <summary>
/// Codificación Base32 en el alfabeto de Crockford para los códigos de equipo y las claves de licencia.
/// <para>Se elige Crockford —y no el Base32 clásico ni Base64— porque estos códigos los DICTAN y TECLEAN
/// personas: su alfabeto excluye las letras confundibles (<c>I</c>, <c>L</c>, <c>O</c>, <c>U</c>) y al decodificar
/// acepta las confusiones típicas (<c>I</c>/<c>l</c> → 1, <c>O</c> → 0), además de minúsculas, guiones y espacios.
/// Así «pásame tu código por teléfono» deja de ser una fuente de errores de soporte. Sin relleno («=»).</para>
/// </summary>
public static class Base32Crockford
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // sin I, L, O, U

    /// <summary>Codifica los bytes en Base32 Crockford (sin relleno).</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return string.Empty;
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]); // últimos bits, rellenados con ceros
        return sb.ToString();
    }

    /// <summary>
    /// Decodifica una cadena Base32 Crockford. Tolera minúsculas, guiones, espacios y las confusiones habituales
    /// (I/l→1, O→0). Devuelve <c>false</c> si aparece cualquier carácter no válido, en vez de lanzar: el texto
    /// viene de un cuadro de pegado y un error de tecleo es lo NORMAL, no una excepción.
    /// </summary>
    public static bool TryDecode(string? text, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        var bytes = new List<byte>(text.Length * 5 / 8 + 1);
        int buffer = 0, bits = 0;
        foreach (char raw in text)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n') continue; // separadores de legibilidad
            char c = char.ToUpperInvariant(raw);
            c = c switch { 'I' or 'L' => '1', 'O' => '0', _ => c }; // confusiones típicas al teclear
            int v = Alphabet.IndexOf(c);
            if (v < 0) return false;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        data = bytes.ToArray();
        return data.Length > 0;
    }

    /// <summary>
    /// Decodificación ESTRICTA: además de lo que hace <see cref="TryDecode"/>, exige que el número de caracteres
    /// significativos sea EXACTAMENTE el que corresponde a <paramref name="expectedBytes"/> y que los bits
    /// sobrantes del último carácter sean cero.
    /// <para>Hace falta para las claves de licencia: sin esto, añadirle caracteres a una clave válida produciría
    /// otra cadena que también decodifica a los mismos bytes, así que la clave no tendría forma canónica y
    /// «claves» distintas valdrían por igual.</para>
    /// </summary>
    public static bool TryDecodeExact(string? text, int expectedBytes, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text) || expectedBytes <= 0) return false;

        int significant = 0;
        foreach (char c in text)
            if (c is not ('-' or ' ' or '\t' or '\r' or '\n')) significant++;
        if (significant != (expectedBytes * 8 + 4) / 5) return false;

        if (!TryDecode(text, out var raw)) return false;
        // TryDecode descarta los bits residuales; aquí exigimos además que fueran CERO para que la codificación
        // sea única. Se recomprueba re-codificando: si el texto era canónico, coincide carácter a carácter.
        if (raw.Length < expectedBytes) return false;
        var exact = raw.AsSpan(0, expectedBytes).ToArray();
        if (!string.Equals(Encode(exact), Normalize(text), StringComparison.Ordinal)) return false;

        data = exact;
        return true;
    }

    /// <summary>Quita separadores y aplica el mismo mapeo de confusiones que la decodificación, para poder
    /// comparar el texto recibido con su forma canónica.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char raw in text)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n') continue;
            char c = char.ToUpperInvariant(raw);
            sb.Append(c switch { 'I' or 'L' => '1', 'O' => '0', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>Parte el texto en grupos separados por guiones (p. ej. de 4 en 4 o de 16 en 16) para que se
    /// pueda leer y teclear sin perderse.</summary>
    public static string Group(string text, int size)
    {
        if (size <= 0 || text.Length <= size) return text;
        var sb = new StringBuilder(text.Length + text.Length / size);
        for (int i = 0; i < text.Length; i += size)
        {
            if (i > 0) sb.Append('-');
            sb.Append(text, i, Math.Min(size, text.Length - i));
        }
        return sb.ToString();
    }
}
