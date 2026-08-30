using System.Security.Cryptography;
using Baioss.Record.Application.Licensing;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Formato y verificación de la clave de licencia. Lo que de verdad hay que demostrar aquí es el requisito del
/// producto: <b>una licencia emitida para un equipo NO vale en otro</b>, y una clave manipulada no cuela.
/// </summary>
public sealed class LicenseKeyTests
{
    private static readonly byte[] MachineA = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    private static readonly byte[] MachineB = { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };

    /// <summary>Emite una licencia como haría la herramienta del proveedor (con la clave PRIVADA).</summary>
    private static (string Key, string PublicKeyBase64) Issue(byte[] fingerprint, ECDsa? signer = null, byte channels = 4)
    {
        var ecdsa = signer ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedOn = new DateOnly(2026, 8, 3);
        var message = LicenseKey.BuildSignedMessage(fingerprint, LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, channels);
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string key = LicenseKey.Encode(LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, channels, signature);
        string pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        if (signer is null) ecdsa.Dispose();
        return (key, pub);
    }

    [Fact]
    public void LicenseIssuedForThisMachine_IsAccepted()
    {
        var (key, pub) = Issue(MachineA);
        Assert.Equal(LicenseRejection.None, LicenseKey.Verify(key, MachineA, pub));
    }

    [Fact]
    public void LicenseIssuedForAnotherMachine_IsRejected()
    {
        // EL REQUISITO: la misma clave, copiada a otro PC, no debe funcionar. Como la huella entra en el mensaje
        // firmado (aunque no viaje en la clave), en otro equipo el mensaje difiere y la firma no valida.
        var (key, pub) = Issue(MachineA);
        Assert.Equal(LicenseRejection.OtherMachine, LicenseKey.Verify(key, MachineB, pub));
    }

    [Fact]
    public void KeyFromADifferentIssuer_IsRejected()
    {
        // Una clave firmada con OTRA privada (alguien intentando fabricar licencias) no valida con nuestra pública.
        var (key, _) = Issue(MachineA);
        using var otherIssuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string foreignPublic = Convert.ToBase64String(otherIssuer.ExportSubjectPublicKeyInfo());
        Assert.NotEqual(LicenseRejection.None, LicenseKey.Verify(key, MachineA, foreignPublic));
    }

    [Fact]
    public void TamperedKey_IsRejected()
    {
        var (key, pub) = Issue(MachineA);
        // Cambia un carácter del cuerpo de la firma (evitando los guiones de agrupación).
        var chars = key.ToCharArray();
        int i = key.Length - 5;
        chars[i] = chars[i] == 'A' ? 'B' : 'A';
        Assert.NotEqual(LicenseRejection.None, LicenseKey.Verify(new string(chars), MachineA, pub));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-es-una-licencia")]
    [InlineData("XXXX")]
    public void MalformedKey_IsReportedAsMalformed_NotAsCrash(string text)
        => Assert.Equal(LicenseRejection.Malformed, LicenseKey.Verify(text, MachineA, "AAAA"));

    [Fact]
    public void RandomKeys_NeverThrow_TheyAreJustRejected()
    {
        // REGRESIÓN: el campo de fecha de la clave lo controla quien teclea y se decodifica ANTES de verificar la
        // firma; con un valor fuera de rango, DateOnly.AddDays LANZABA. Como esto lo alimenta el botón «Activar»
        // y un endpoint HTTP, una excepción aquí rompía la ventana y devolvía un 500. Un «TryX» no puede lanzar.
        var rng = new Random(1234);
        var chars = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (int i = 0; i < 500; i++)
        {
            var key = new string(Enumerable.Range(0, 114).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
            var rejection = LicenseKey.Verify(key, MachineA, "AAAA"); // no debe lanzar
            Assert.NotEqual(LicenseRejection.None, rejection);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("00")]
    [InlineData("ZZZ")]
    public void KeyWithExtraCharacters_IsRejected_SoTheKeyHasOneCanonicalForm(string suffix)
    {
        // REGRESIÓN: la decodificación descartaba los bits sobrantes y aceptaba longitudes de más, así que
        // «licencia + basura» seguía validando y cada licencia tenía miles de formas equivalentes.
        var (key, pub) = Issue(MachineA);
        Assert.Equal(LicenseRejection.None, LicenseKey.Verify(key, MachineA, pub));
        Assert.NotEqual(LicenseRejection.None, LicenseKey.Verify(key + suffix, MachineA, pub));
    }

    [Fact]
    public void EmptyFingerprint_IsRefused_ThereIsNoSuchThingAsAMasterLicense()
    {
        // Con huella vacía el mensaje firmado sería idéntico en TODOS los equipos: una licencia maestra universal.
        Assert.Throws<ArgumentException>(() =>
            LicenseKey.BuildSignedMessage(Array.Empty<byte>(), LicenseKey.CurrentVersion, LicenseType.Lifetime, new DateOnly(2026, 8, 3), 4));

        var (key, pub) = Issue(MachineA);
        Assert.NotEqual(LicenseRejection.None, LicenseKey.Verify(key, Array.Empty<byte>(), pub));
    }

    [Fact]
    public void LicenseOfAFutureType_IsRejected_NotAcceptedAsPerpetual()
    {
        // El byte de tipo existe para poder emitir licencias TEMPORALES en el futuro sin cambiar la versión del
        // formato. Esta build no sabe caducarlas: si aceptara un tipo desconocido, una temporal futura validaría
        // aquí como PERPETUA. Debe rechazarse como «necesitas una versión más nueva», con la firma siendo válida.
        var futureType = (LicenseType)7;
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedOn = new DateOnly(2026, 8, 3);
        var message = LicenseKey.BuildSignedMessage(MachineA, LicenseKey.CurrentVersion, futureType, issuedOn, 4);
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string key = LicenseKey.Encode(LicenseKey.CurrentVersion, futureType, issuedOn, 4, signature);
        string pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        Assert.Equal(LicenseRejection.UnsupportedVersion, LicenseKey.Verify(key, MachineA, pub));
    }

    [Fact]
    public void VersionZero_IsMalformed()
    {
        // La versión 0 no existe (la primera es la 1): solo puede ser una clave corrompida, aunque venga firmada.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedOn = new DateOnly(2026, 8, 3);
        var message = LicenseKey.BuildSignedMessage(MachineA, 0, LicenseType.Lifetime, issuedOn, 4);
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string key = LicenseKey.Encode(0, LicenseType.Lifetime, issuedOn, 4, signature);
        string pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        Assert.Equal(LicenseRejection.Malformed, LicenseKey.Verify(key, MachineA, pub));
    }

    [Fact]
    public void KeyIsReadable_GroupedAndWithoutConfusableCharacters()
    {
        var (key, _) = Issue(MachineA);
        Assert.Contains('-', key);                                   // en grupos, para poder leerla
        foreach (char c in key.Replace("-", ""))
            Assert.DoesNotContain(c, "ILOU");                        // sin caracteres confundibles
        Assert.Equal(114, key.Replace("-", "").Length);              // 71 bytes en Base32
    }

    [Fact]
    public void DecodedPayload_KeepsTypeIssueDateAndChannels()
    {
        var (key, _) = Issue(MachineA, channels: 2);
        Assert.True(LicenseKey.TryDecode(key, out var payload));
        Assert.NotNull(payload);
        Assert.Equal(LicenseType.Lifetime, payload!.Type);
        Assert.Equal(new DateOnly(2026, 8, 3), payload.IssuedOn);
        Assert.Equal(LicenseKey.CurrentVersion, payload.Version);
        Assert.Equal(2, payload.Channels);
    }

    // --- Canales pagados: viajan FIRMADOS dentro de la clave (el precio del producto depende de ellos) ---

    [Fact]
    public void Channels_AreSigned_UpgradingThemByHandInvalidatesTheKey()
    {
        // El cliente pagó 2 canales e intenta fabricarse la clave «de 4»: re-codifica la MISMA firma con el
        // byte de canales cambiado. La firma cubre los canales, así que la clave resultante NO valida.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedOn = new DateOnly(2026, 8, 3);
        var message = LicenseKey.BuildSignedMessage(MachineA, LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, 2);
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        string honest = LicenseKey.Encode(LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, 2, signature);
        string forged = LicenseKey.Encode(LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, 4, signature);

        Assert.Equal(LicenseRejection.None, LicenseKey.Verify(honest, MachineA, pub));
        Assert.NotEqual(LicenseRejection.None, LicenseKey.Verify(forged, MachineA, pub));
    }

    [Fact]
    public void ZeroChannels_IsRefusedAtIssueTime_AndMalformedAtVerifyTime()
    {
        // Una licencia de cero canales no significa nada: la emisión la rechaza y, si llegara fabricada, se
        // trata como clave corrupta.
        Assert.Throws<ArgumentException>(() =>
            LicenseKey.BuildSignedMessage(MachineA, LicenseKey.CurrentVersion, LicenseType.Lifetime, new DateOnly(2026, 8, 3), 0));

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedOn = new DateOnly(2026, 8, 3);
        var message = LicenseKey.BuildSignedMessage(MachineA, LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, 1);
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string forged = LicenseKey.Encode(LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, 0, signature);
        string pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        Assert.Equal(LicenseRejection.Malformed, LicenseKey.Verify(forged, MachineA, pub));
    }

    [Fact]
    public void UserTypos_AreTolerated_LowercaseSpacesAndConfusables()
    {
        // El operador copia la clave a mano: minúsculas, espacios y O/0 · I/1 deben seguir funcionando.
        var (key, pub) = Issue(MachineA);
        string mangled = key.ToLowerInvariant().Replace("-", " ");
        Assert.Equal(LicenseRejection.None, LicenseKey.Verify(mangled, MachineA, pub));
    }
}
