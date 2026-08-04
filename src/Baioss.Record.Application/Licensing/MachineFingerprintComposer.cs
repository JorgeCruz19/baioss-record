using System.Security.Cryptography;
using System.Text;

namespace Baioss.Record.Application.Licensing;

/// <summary>
/// Composición PURA de la huella del equipo a partir de los identificadores leídos del sistema. Separada de la
/// lectura (que es específica de Windows) para poder probar a fondo lo que de verdad importa: que la huella sea
/// DETERMINISTA y NO AMBIGUA.
///
/// <para><b>Por qué con etiqueta y longitud.</b> Concatenar los identificadores «a pelo» hace que composiciones
/// distintas colisionen: {guid=ABC, placa=«», cpu=DEF} y {guid=ABC, placa=DEF, cpu=«»} producirían el MISMO hash,
/// es decir, dos equipos distintos con la misma huella. Cada componente se emite SIEMPRE, en orden fijo y con su
/// longitud por delante, igual que el mensaje que se firma en <see cref="LicenseKey.BuildSignedMessage"/>.</para>
///
/// <para><b>Valores basura.</b> Muchos equipos (sobre todo de integrador e industriales) devuelven cosas como
/// «To Be Filled By O.E.M.» o «Default string» en los números de serie. Tomarlos por identificadores haría que
/// TODOS esos equipos compartieran huella, así que se descartan y se tratan como ausentes.</para>
///
/// <para><b>Límite conocido y asumido.</b> El identificador más fuerte de Windows (MachineGuid) se CLONA junto
/// con la imagen del disco: dos PCs clonados sin Sysprep comparten huella y, por tanto, licencia. Una licencia
/// offline no puede evitarlo; se documenta a propósito y se mitiga por el lado del proveedor (llevar registro de
/// a qué código de equipo se ha emitido cada licencia).</para>
/// </summary>
public static class MachineFingerprintComposer
{
    /// <summary>Longitud de la huella en bytes: 10 bytes = 80 bits, de sobra para descartar colisiones y corta
    /// como código legible de 16 caracteres.</summary>
    public const int Length = 10;

    private static readonly byte[] DomainTag = Encoding.ASCII.GetBytes("BAIOSS-RECORD-FINGERPRINT-v1");

    /// <summary>Valores que el hardware devuelve cuando NO hay número de serie de verdad. Se tratan como ausentes.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "", "0", "00000000", "N/A", "NA", "NONE", "NULL", "INVALID", "NOT APPLICABLE", "NOT SPECIFIED",
        "TO BE FILLED BY O.E.M.", "TO BE FILLED BY OEM", "DEFAULT STRING", "SYSTEM SERIAL NUMBER",
        "BASE BOARD SERIAL NUMBER", "CHASSIS SERIAL NUMBER", "UNKNOWN", "FILLED BY OEM",
    };

    /// <summary>
    /// Huella a partir de los componentes EN ORDEN FIJO. Un componente ausente o basura se emite como vacío, de
    /// modo que la huella sigue siendo estable y no ambigua aunque el equipo no exponga alguno.
    /// </summary>
    public static byte[] Compose(params string?[] components)
    {
        var buffer = new List<byte>(256);
        buffer.AddRange(DomainTag);
        foreach (var c in components)
        {
            var value = Normalize(c);
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 255) bytes = bytes[..255];
            buffer.Add((byte)bytes.Length); // prefijo de longitud: elimina la ambigüedad de la concatenación
            buffer.AddRange(bytes);
        }
        return SHA256.HashData(buffer.ToArray())[..Length];
    }

    /// <summary>Normaliza un identificador del sistema: recorta, pasa a mayúsculas, colapsa espacios y descarta
    /// los valores de relleno del hardware. Devuelve "" si no es un identificador utilizable.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var value = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return Placeholders.Contains(value) ? "" : value;
    }

    /// <summary>Código legible que el cliente envía al proveedor (16 caracteres en 4 grupos).</summary>
    public static string ToCode(ReadOnlySpan<byte> fingerprint) => Base32Crockford.Group(Base32Crockford.Encode(fingerprint), 4);

    /// <summary>¿Aporta la huella identificadores REALES? Si no hay ninguno, el equipo no es distinguible y la
    /// app debe avisar (no se puede atar una licencia a algo que no identifica nada).</summary>
    public static bool HasAnyRealIdentifier(params string?[] components) => components.Any(c => Normalize(c).Length > 0);
}
