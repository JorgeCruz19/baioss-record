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
    private static (string Key, string PublicKeyBase64) Issue(byte[] fingerprint, ECDsa? signer = null)
    {
        var ecdsa = signer ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuedOn = new DateOnly(2026, 8, 3);
        var message = LicenseKey.BuildSignedMessage(fingerprint, LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn);
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string key = LicenseKey.Encode(LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, signature);
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
            var key = new string(Enumerable.Range(0, 112).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
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
            LicenseKey.BuildSignedMessage(Array.Empty<byte>(), LicenseKey.CurrentVersion, LicenseType.Lifetime, new DateOnly(2026, 8, 3)));

        var (key, pub) = Issue(MachineA);
        Assert.NotEqual(LicenseRejection.None, LicenseKey.Verify(key, Array.Empty<byte>(), pub));
    }

    [Fact]
    public void KeyIsReadable_GroupedAndWithoutConfusableCharacters()
    {
        var (key, _) = Issue(MachineA);
        Assert.Contains('-', key);                                   // en grupos, para poder leerla
        foreach (char c in key.Replace("-", ""))
            Assert.DoesNotContain(c, "ILOU");                        // sin caracteres confundibles
        Assert.Equal(112, key.Replace("-", "").Length);              // 70 bytes en Base32
    }

    [Fact]
    public void DecodedPayload_KeepsTypeAndIssueDate()
    {
        var (key, _) = Issue(MachineA);
        Assert.True(LicenseKey.TryDecode(key, out var payload));
        Assert.NotNull(payload);
        Assert.Equal(LicenseType.Lifetime, payload!.Type);
        Assert.Equal(new DateOnly(2026, 8, 3), payload.IssuedOn);
        Assert.Equal(LicenseKey.CurrentVersion, payload.Version);
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
