using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Baioss.Record.Application.Licensing;

/// <summary>Tipo de licencia. Hoy solo perpetua; el byte queda para poder añadir temporales sin romper el formato.</summary>
public enum LicenseType : byte
{
    Lifetime = 1,
}

/// <summary>Contenido de una clave de licencia ya decodificada (sin verificar todavía).</summary>
public sealed record LicensePayload(byte Version, LicenseType Type, DateOnly IssuedOn, byte[] Signature);

/// <summary>
/// Formato de la clave de licencia y su verificación.
///
/// <para><b>Qué se firma.</b> El mensaje firmado incluye la HUELLA DEL EQUIPO pero esta NO viaja dentro de la
/// clave: al verificar se usa la huella de la máquina donde se está activando. Consecuencia buscada: la misma
/// clave copiada a otro PC produce un mensaje distinto y la firma NO valida ⇒ una licencia solo sirve en el
/// equipo para el que se emitió, sin necesidad de conexión ni servidor. Además acorta la clave 16 caracteres.</para>
///
/// <para><b>Por qué asimétrica (ECDSA P-256) y no un HMAC.</b> Con HMAC, el secreto tendría que viajar DENTRO de
/// la app y cualquiera podría extraerlo y fabricarse licencias. Con firma asimétrica la app solo lleva la clave
/// PÚBLICA: sirve para verificar, no para emitir. La privada se queda en la herramienta del proveedor.</para>
///
/// <para><b>Formato en el cable.</b> version(1) ‖ tipo(1) ‖ díaEmisión(4, LE) ‖ firma(64, IEEE P1363 r‖s) = 70 bytes
/// → Base32 Crockford = 112 caracteres, mostrados en 7 grupos de 16.</para>
/// </summary>
public static class LicenseKey
{
    /// <summary>Versión del formato que emite y entiende esta build.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Tamaño de la firma ECDSA P-256 en formato fijo r‖s.</summary>
    public const int SignatureLength = 64;

    private const int HeaderLength = 6;                       // version + tipo + díaEmisión
    private const int TotalLength = HeaderLength + SignatureLength;
    private static readonly DateOnly Epoch = new(2020, 1, 1); // origen del contador de días

    /// <summary>Separador de DOMINIO: evita que una firma hecha para otro propósito (u otra versión del formato)
    /// pueda reinterpretarse como una licencia válida.</summary>
    private static readonly byte[] DomainTag = Encoding.ASCII.GetBytes("BAIOSS-RECORD-LICENSE-v1");

    /// <summary>
    /// Mensaje EXACTO que se firma y se verifica: separador de dominio ‖ longitud de la huella ‖ huella ‖
    /// version ‖ tipo ‖ díaEmisión. Incluir la LONGITUD de la huella evita ambigüedad (que dos combinaciones
    /// distintas de huella+cabecera produzcan la misma secuencia de bytes).
    /// </summary>
    public static byte[] BuildSignedMessage(ReadOnlySpan<byte> fingerprint, byte version, LicenseType type, DateOnly issuedOn)
    {
        // Guarda deliberada: con una huella VACÍA el mensaje sería idéntico en cualquier equipo, es decir, una
        // licencia MAESTRA universal. Nunca debe poder construirse, ni al emitir ni al verificar.
        if (fingerprint.Length is 0 or > 255)
            throw new ArgumentException("La huella del equipo debe medir entre 1 y 255 bytes.", nameof(fingerprint));

        var msg = new byte[DomainTag.Length + 1 + fingerprint.Length + HeaderLength];
        int o = 0;
        DomainTag.CopyTo(msg, o); o += DomainTag.Length;
        msg[o++] = (byte)fingerprint.Length;
        fingerprint.CopyTo(msg.AsSpan(o)); o += fingerprint.Length;
        msg[o++] = version;
        msg[o++] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(o), DaysFromEpoch(issuedOn));
        return msg;
    }

    /// <summary>Compone la clave legible a partir de la cabecera y la firma.</summary>
    public static string Encode(byte version, LicenseType type, DateOnly issuedOn, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength)
            throw new ArgumentException($"La firma debe medir {SignatureLength} bytes (formato fijo r‖s).", nameof(signature));

        var raw = new byte[TotalLength];
        raw[0] = version;
        raw[1] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(2), DaysFromEpoch(issuedOn));
        signature.CopyTo(raw.AsSpan(HeaderLength));
        return Base32Crockford.Group(Base32Crockford.Encode(raw), 16);
    }

    /// <summary>
    /// Decodifica la clave (sin verificar la firma). <c>false</c> —nunca una excepción— si el texto no es una
    /// clave bien formada: viene de un cuadro de pegado y equivocarse es lo normal.
    /// <para>La decodificación es ESTRICTA (longitud exacta y sin bits sobrantes): si no lo fuera, añadir
    /// caracteres al final de una clave válida produciría otra clave que también valida, y la clave dejaría de
    /// tener una forma canónica.</para>
    /// </summary>
    public static bool TryDecode(string? text, out LicensePayload? payload)
    {
        payload = null;
        if (!Base32Crockford.TryDecodeExact(text, TotalLength, out var raw)) return false;

        // El contador de días viene de una clave que puede estar corrupta: hay que validar el RANGO antes de
        // construir la fecha. Sin esto, DateOnly.AddDays LANZA con casi cualquier clave mal tecleada — justo lo
        // que un método «TryX» promete que no hará.
        uint day = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(2));
        if (day > MaxDayOffset) return false;

        var signature = new byte[SignatureLength];
        Array.Copy(raw, HeaderLength, signature, 0, SignatureLength);
        payload = new LicensePayload(raw[0], (LicenseType)raw[1], Epoch.AddDays((int)day), signature);
        return true;
    }

    /// <summary>Días representables desde <see cref="Epoch"/> sin desbordar <see cref="DateOnly"/>.</summary>
    private static readonly uint MaxDayOffset = (uint)(DateOnly.MaxValue.DayNumber - Epoch.DayNumber);

    /// <summary>
    /// Verifica una clave contra la huella de ESTE equipo usando la clave pública del proveedor (SubjectPublicKeyInfo
    /// en Base64). Devuelve el motivo exacto del rechazo para poder dar un mensaje útil al operador.
    /// </summary>
    public static LicenseRejection Verify(string? licenseKey, ReadOnlySpan<byte> fingerprint, string publicKeyBase64)
    {
        if (fingerprint.Length is 0 or > 255) return LicenseRejection.BadSignature; // sin huella no se valida nada
        if (!TryDecode(licenseKey, out var payload) || payload is null) return LicenseRejection.Malformed;
        if (payload.Version is 0) return LicenseRejection.Malformed; // la versión 0 no existe: clave corrupta
        if (payload.Version > CurrentVersion) return LicenseRejection.UnsupportedVersion;

        // Un TIPO que esta build no conoce se rechaza IGUAL que una versión futura. El byte de tipo existe para
        // añadir licencias temporales sin cambiar la versión del formato: si esta guarda no estuviera, una
        // licencia temporal emitida en el futuro validaría aquí como PERPETUA (esta build no tiene noción de
        // caducidad), y las builds ya desplegadas no se pueden corregir a posteriori.
        if (!Enum.IsDefined(payload.Type)) return LicenseRejection.UnsupportedVersion;

        byte[] spki;
        try { spki = Convert.FromBase64String(publicKeyBase64); }
        catch (FormatException) { return LicenseRejection.BadSignature; } // build mal configurada: nunca valida

        // Cinturón y tirantes: este método lo alimenta texto tecleado por el operador y lo llaman el botón
        // «Activar» de la UI y un endpoint HTTP. NINGUNA entrada debe poder propagar una excepción hasta ahí.
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            var message = BuildSignedMessage(fingerprint, payload.Version, payload.Type, payload.IssuedOn);
            bool ok = ecdsa.VerifyData(message, payload.Signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            // Si la firma es auténtica pero NO valida aquí, lo más probable con diferencia es que la clave sea de
            // OTRO equipo (es lo único que cambia el mensaje). No se puede distinguir con certeza sin más datos,
            // así que se informa del caso útil para el soporte.
            return ok ? LicenseRejection.None : LicenseRejection.OtherMachine;
        }
        catch (CryptographicException) { return LicenseRejection.BadSignature; }
        catch (Exception) { return LicenseRejection.Malformed; } // jamás propagar: la entrada la escribe una persona
    }

    private static uint DaysFromEpoch(DateOnly d)
    {
        int days = d.DayNumber - Epoch.DayNumber;
        return days <= 0 ? 0 : (uint)days;
    }
}
