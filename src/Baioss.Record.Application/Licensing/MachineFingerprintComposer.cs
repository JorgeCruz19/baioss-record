using System.Security.Cryptography;
using System.Text;

namespace Baioss.Record.Application.Licensing;

/// <summary>
/// Composición PURA de la huella del equipo a partir de los identificadores leídos del sistema. Separada de la
/// lectura (que es específica de Windows) para poder probar a fondo lo que de verdad importa: que la huella sea
/// DETERMINISTA y NO AMBIGUA.
///
/// <para><b>Dos niveles (huella v2).</b>
/// <list type="bullet">
/// <item><b>Anclada al TPM</b> (<see cref="ComposeTpm"/>): la huella es SOLO la clave pública de endoso del chip
/// TPM 2.0 del equipo, que el fabricante graba en el chip y no vive en el disco. Un clon del disco arranca en otro
/// chip y obtiene otra huella: la licencia no vale en el clon, sin conexión ni servidor. Además sobrevive a
/// reinstalar Windows, cambiar el disco, Sysprep y borrar el TPM; solo cambia al cambiar la placa (o el chip).</item>
/// <item><b>Clásica</b> (<see cref="Compose"/>): para equipos sin TPM 2.0 utilizable. MachineGuid, serial del
/// volumen del sistema, seriales de placa y de equipo, y UUID de sistema del SMBIOS. Los dos primeros viven en el
/// disco y se CLONAN con él; los otros tres vienen del firmware, pero muchos equipos de integrador los traen de
/// relleno. En esos equipos un clon del disco comparte huella y licencia: es el límite conocido de este nivel.</item>
/// </list>
/// El bit alto del primer byte dice de qué nivel es una huella (<see cref="IsTpmAnchored"/>): viaja en el código
/// de equipo, así que el proveedor lo ve al emitir (y puede pedir que se active el TPM antes de vender).</para>
///
/// <para><b>Por qué con etiqueta y longitud.</b> Concatenar los identificadores «a pelo» hace que composiciones
/// distintas colisionen: {guid=ABC, placa=«», serie=DEF} y {guid=ABC, placa=DEF, serie=«»} producirían el MISMO
/// hash, es decir, dos equipos distintos con la misma huella. Cada componente se emite SIEMPRE, en orden fijo y
/// con su longitud por delante, igual que el mensaje que se firma en <see cref="LicenseKey.BuildSignedMessage"/>.</para>
///
/// <para><b>Valores basura.</b> Muchos equipos (sobre todo de integrador e industriales) devuelven cosas como
/// «To Be Filled By O.E.M.» o «Default string» en los números de serie, y UUID todo ceros, todo FF o la secuencia
/// 00020003-0004-…. Tomarlos por identificadores haría que TODOS esos equipos compartieran huella, así que se
/// descartan y se tratan como ausentes.</para>
///
/// <para><b>Cambiar esta composición invalida todas las licencias emitidas</b> (la huella va dentro del mensaje
/// firmado). La v2 se introdujo antes de la primera venta.</para>
/// </summary>
public static class MachineFingerprintComposer
{
    /// <summary>Longitud de la huella en bytes: 10 bytes = 80 bits (79 de hash + 1 de nivel), de sobra para
    /// descartar colisiones y corta como código legible de 16 caracteres.</summary>
    public const int Length = 10;

    /// <summary>Componentes de la huella clásica, EN ORDEN: MachineGuid, serial del volumen del sistema, serial de
    /// placa, serial de equipo y UUID de sistema del SMBIOS.</summary>
    public const int ClassicComponentCount = 5;

    private static readonly byte[] DomainTag = Encoding.ASCII.GetBytes("BAIOSS-RECORD-FINGERPRINT-v2");
    private const byte TpmFlag = 0x80;

    /// <summary>Valores que el hardware devuelve cuando NO hay número de serie de verdad. Se tratan como ausentes.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "", "0", "00000000", "N/A", "NA", "NONE", "NULL", "INVALID", "NOT APPLICABLE", "NOT SPECIFIED",
        "TO BE FILLED BY O.E.M.", "TO BE FILLED BY OEM", "DEFAULT STRING", "SYSTEM SERIAL NUMBER",
        "BASE BOARD SERIAL NUMBER", "CHASSIS SERIAL NUMBER", "UNKNOWN", "FILLED BY OEM",
        // UUID de sistema del SMBIOS (32 dígitos hexadecimales, en el orden de bytes de la tabla): sin programar
        // (todo ceros o todo FF) o con la secuencia de ejemplo que muchas placas genéricas dejan tal cual.
        "00000000000000000000000000000000", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", "00020003000400050006000700080009",
    };

    /// <summary>
    /// Huella CLÁSICA a partir de los componentes EN ORDEN FIJO (ver <see cref="ClassicComponentCount"/>). Un
    /// componente ausente o basura se emite como vacío, de modo que la huella sigue siendo estable y no ambigua
    /// aunque el equipo no exponga alguno. El bit de nivel queda a 0.
    /// </summary>
    public static byte[] Compose(params string?[] components)
    {
        var buffer = new List<byte>(256);
        buffer.AddRange(DomainTag);
        buffer.Add((byte)'C'); // nivel clásico: su entrada nunca coincide con la de una huella anclada al TPM
        foreach (var c in components)
        {
            var value = Normalize(c);
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 255) bytes = bytes[..255];
            buffer.Add((byte)bytes.Length); // prefijo de longitud: elimina la ambigüedad de la concatenación
            buffer.AddRange(bytes);
        }
        var fingerprint = SHA256.HashData(buffer.ToArray())[..Length];
        fingerprint[0] &= unchecked((byte)~TpmFlag);
        return fingerprint;
    }

    /// <summary>
    /// Huella ANCLADA AL TPM: solo la clave pública de endoso del chip (su «área pública» tal como la devuelve el
    /// TPM). El bit de nivel queda a 1.
    /// </summary>
    public static byte[] ComposeTpm(ReadOnlySpan<byte> endorsementKeyPublicArea)
    {
        // Con un área vacía la huella sería la misma en cualquier equipo: una licencia universal. Nunca debe poder construirse.
        if (endorsementKeyPublicArea.IsEmpty)
            throw new ArgumentException("La clave de endoso del TPM no puede estar vacía.", nameof(endorsementKeyPublicArea));
        var buffer = new byte[DomainTag.Length + 1 + SHA256.HashSizeInBytes];
        DomainTag.CopyTo(buffer, 0);
        buffer[DomainTag.Length] = (byte)'T';
        SHA256.HashData(endorsementKeyPublicArea, buffer.AsSpan(DomainTag.Length + 1));
        var fingerprint = SHA256.HashData(buffer)[..Length];
        fingerprint[0] |= TpmFlag;
        return fingerprint;
    }

    /// <summary>¿Es una huella anclada al TPM? (el bit de nivel). False para una clásica o de longitud inesperada.</summary>
    public static bool IsTpmAnchored(ReadOnlySpan<byte> fingerprint) => fingerprint.Length == Length && (fingerprint[0] & TpmFlag) != 0;

    /// <summary>Lo mismo a partir del CÓDIGO de equipo legible; <c>null</c> si el texto no es un código válido.</summary>
    public static bool? IsTpmAnchoredCode(string? code)
        => Base32Crockford.TryDecodeExact(code, Length, out var raw) ? IsTpmAnchored(raw) : null;

    /// <summary>
    /// ¿Es la huella de «un equipo sin NINGÚN identificador»? Es una constante (la misma en todos los equipos en
    /// ese estado): emitir una licencia para ella sería vender una licencia universal.
    /// </summary>
    public static bool IsGeneric(ReadOnlySpan<byte> fingerprint) => fingerprint.SequenceEqual(Compose(new string?[ClassicComponentCount]));

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
