using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baioss.Record.Application.Licensing;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// El manifiesto de integridad, en puro: lo que el proveedor firma y lo que la app concluye. Lo que importa en un
/// grabador 24/7 es la asimetría del veredicto:
/// <list type="bullet">
/// <item><b>Tampered</b> SOLO con un manifiesto auténtico y un archivo leído con éxito que no coincide (evidencia
/// positiva). Nada más puede producirlo.</item>
/// <item>Todo lo demás —manifiesto editado, firmado por otro, de otra versión, basura, clave pública rota, archivo
/// ausente o ilegible— es <b>Unverified</b>, que no cambia nada.</item>
/// <item>Nunca lanza, venga lo que venga.</item>
/// </list>
/// </summary>
public sealed class IntegrityManifestTests
{
    private static byte[] Bytes(int seed)
    {
        var raw = new byte[4096 + seed];
        new Random(seed).NextBytes(raw);
        return raw;
    }

    private static string Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>Una «carpeta publicada» en memoria: nombre → bytes. Se resume como lo haría el disco.</summary>
    private static Dictionary<string, byte[]> Install()
        => IntegrityManifest.SensitiveFiles.Select((name, i) => (name, bytes: Bytes(i + 1)))
            .ToDictionary(f => f.name, f => f.bytes, StringComparer.Ordinal);

    private static Dictionary<string, string> Hashes(IReadOnlyDictionary<string, byte[]> files)
        => files.ToDictionary(f => f.Key, f => Hex(f.Value), StringComparer.Ordinal);

    private static Func<string, string?> Reader(IReadOnlyDictionary<string, byte[]> files)
        => name => files.TryGetValue(name, out var content) ? Hex(content) : null;

    /// <summary>El proveedor: la misma clave que firma licencias firma manifiestos.</summary>
    private sealed class Provider : IDisposable
    {
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
        public string Sign(IReadOnlyDictionary<string, string> hashes) => IntegrityManifest.Sign(hashes, Key, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        public void Dispose() => Key.Dispose();
    }

    [Fact]
    public void ASignedManifest_IsIntact_WhenEveryFileMatches()
    {
        using var provider = new Provider();
        var files = Install();
        string blob = provider.Sign(Hashes(files));

        var report = IntegrityManifest.Verify(blob, provider.PublicKey, Reader(files));

        Assert.Equal(IntegrityVerdict.Intact, report.Verdict);
        Assert.Equal(IntegrityManifest.SensitiveFiles.Count, report.Files.Count);
        Assert.All(report.Files, f => Assert.Equal(IntegrityFileStatus.Match, f.Status));
        Assert.Empty(report.MismatchedFiles);
        Assert.DoesNotContain('\n', blob); // una sola línea en disco
    }

    [Fact]
    public void APatchedFile_IsTampered_AndIsNamed()
    {
        using var provider = new Provider();
        var files = Install();
        string blob = provider.Sign(Hashes(files));
        // Alguien parchea Infrastructure (un byte basta) y deja el manifiesto como estaba.
        files["Baioss.Record.Infrastructure.dll"] = files["Baioss.Record.Infrastructure.dll"].Concat(new byte[] { 0x90 }).ToArray();

        var report = IntegrityManifest.Verify(blob, provider.PublicKey, Reader(files));

        Assert.Equal(IntegrityVerdict.Tampered, report.Verdict);
        Assert.Equal(new[] { "Baioss.Record.Infrastructure.dll" }, report.MismatchedFiles);
        Assert.Contains("Baioss.Record.Infrastructure.dll", report.Detail);
        Assert.Equal(IntegrityManifest.SensitiveFiles.Count - 1, report.Files.Count(f => f.Status == IntegrityFileStatus.Match));
    }

    [Fact]
    public void AnEditedManifest_IsUnverified_NotTampered_BecauseTheSignatureNoLongerCovers()
    {
        // El atacante parchea la DLL Y recomputa su resumen en el manifiesto: sin la privada, la firma deja de cuadrar.
        using var provider = new Provider();
        var files = Install();
        string blob = provider.Sign(Hashes(files));
        files["Baioss.Record.Application.dll"] = Bytes(99);

        int dot = blob.IndexOf('.');
        var payload = JsonSerializer.Deserialize<IntegrityManifest.Payload>(Convert.FromBase64String(blob[..dot]))!;
        payload.Files["Baioss.Record.Application.dll"] = Hex(files["Baioss.Record.Application.dll"]);
        string forged = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)) + blob[dot..];

        var report = IntegrityManifest.Verify(forged, provider.PublicKey, Reader(files));

        Assert.Equal(IntegrityVerdict.Unverified, report.Verdict);
        Assert.Contains("firma", report.Detail);
        Assert.Empty(report.Files); // ni se miran los archivos de un manifiesto que no es del proveedor
    }

    [Fact]
    public void AManifestSignedByAnotherProvider_IsUnverified()
    {
        using var real = new Provider();
        using var forger = new Provider();
        var files = Install();
        string blob = forger.Sign(Hashes(files));

        var report = IntegrityManifest.Verify(blob, real.PublicKey, Reader(files));

        Assert.Equal(IntegrityVerdict.Unverified, report.Verdict);
        Assert.Contains("firma", report.Detail);
    }

    [Fact]
    public void ASignatureWithoutTheDomainTag_IsRejected_SoLicenseSignaturesCannotBecomeManifests()
    {
        // La misma clave firma licencias y manifiestos; el separador de dominio es lo que impide reinterpretar una
        // firma hecha para otra cosa. Una firma sobre el contenido «a pelo» (sin separador) no vale.
        using var provider = new Provider();
        var files = Install();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new IntegrityManifest.Payload(IntegrityManifest.CurrentVersion, "2026-10-09T12:00:00Z", Hashes(files)));
        byte[] rawSignature = provider.Key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string blob = Convert.ToBase64String(json) + "." + Convert.ToBase64String(rawSignature);

        Assert.Equal(IntegrityVerdict.Unverified, IntegrityManifest.Verify(blob, provider.PublicKey, Reader(files)).Verdict);

        // Y la de verdad (con separador) sí: es el mismo contenido.
        byte[] tagged = provider.Key.SignData(IntegrityManifest.BuildSignedMessage(json), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string genuine = Convert.ToBase64String(json) + "." + Convert.ToBase64String(tagged);
        Assert.Equal(IntegrityVerdict.Intact, IntegrityManifest.Verify(genuine, provider.PublicKey, Reader(files)).Verdict);
    }

    [Fact]
    public void AMissingOrUnreadableFile_IsUnverified_ButAMismatchElsewhereIsStillTampered()
    {
        using var provider = new Provider();
        var files = Install();
        string blob = provider.Sign(Hashes(files));

        // Falta un archivo: no se afirma nada sobre él, y el conjunto queda sin verificar (no intacto).
        var missing = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        missing.Remove("Baioss.Record.Engine.FFmpeg.dll");
        var report = IntegrityManifest.Verify(blob, provider.PublicKey, Reader(missing));
        Assert.Equal(IntegrityVerdict.Unverified, report.Verdict);
        Assert.Equal(IntegrityFileStatus.Missing, report.Files.Single(f => f.File == "Baioss.Record.Engine.FFmpeg.dll").Status);
        Assert.Contains("no existe", report.Detail);

        // Un archivo bloqueado (el lector lanza): ilegible, no modificado.
        Func<string, string?> locked = name => name == "Baioss.Record.App.dll" ? throw new IOException("bloqueado por el antivirus") : Reader(files)(name);
        report = IntegrityManifest.Verify(blob, provider.PublicKey, locked);
        Assert.Equal(IntegrityVerdict.Unverified, report.Verdict);
        Assert.Equal(IntegrityFileStatus.Unreadable, report.Files.Single(f => f.File == "Baioss.Record.App.dll").Status);
        Assert.Contains("no se pudo leer", report.Detail);

        // Un archivo bloqueado Y OTRO parcheado: la evidencia positiva manda.
        files["Baioss.Record.App.exe"] = Bytes(77);
        Func<string, string?> lockedAndPatched = name => name == "Baioss.Record.App.dll" ? throw new IOException("bloqueado") : Reader(files)(name);
        report = IntegrityManifest.Verify(blob, provider.PublicKey, lockedAndPatched);
        Assert.Equal(IntegrityVerdict.Tampered, report.Verdict);
        Assert.Equal(new[] { "Baioss.Record.App.exe" }, report.MismatchedFiles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-punto")]
    [InlineData(".firma")]
    [InlineData("contenido.")]
    [InlineData("esto no es base64.tampoco esto")]
    [InlineData("AAAA.AAAA")] // base64 válido, firma de 3 bytes
    public void Garbage_IsUnverified_AndNeverThrows(string? text)
    {
        using var provider = new Provider();
        var report = IntegrityManifest.Verify(text, provider.PublicKey, Reader(Install()));
        Assert.Equal(IntegrityVerdict.Unverified, report.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(report.Detail));
    }

    [Fact]
    public void ABadPublicKey_OrAReaderThatThrows_IsUnverified_AndNeverThrows()
    {
        using var provider = new Provider();
        var files = Install();
        string blob = provider.Sign(Hashes(files));

        // Build mal configurada (clave pública que no es una clave): no se afirma nada.
        Assert.Equal(IntegrityVerdict.Unverified, IntegrityManifest.Verify(blob, "no-es-una-clave", Reader(files)).Verdict);
        Assert.Equal(IntegrityVerdict.Unverified, IntegrityManifest.Verify(blob, Convert.ToBase64String(new byte[] { 1, 2, 3 }), Reader(files)).Verdict);

        // Un lector que revienta con TODO: todo ilegible, nada modificado.
        var report = IntegrityManifest.Verify(blob, provider.PublicKey, _ => throw new UnauthorizedAccessException());
        Assert.Equal(IntegrityVerdict.Unverified, report.Verdict);
        Assert.All(report.Files, f => Assert.Equal(IntegrityFileStatus.Unreadable, f.Status));
    }

    [Fact]
    public void AManifestOfAFutureVersion_OrEmpty_IsUnverified()
    {
        using var provider = new Provider();
        var files = Install();
        static string Blob(ECDsa key, IntegrityManifest.Payload payload)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
            byte[] sig = key.SignData(IntegrityManifest.BuildSignedMessage(json), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Convert.ToBase64String(json) + "." + Convert.ToBase64String(sig);
        }

        var future = IntegrityManifest.Verify(Blob(provider.Key, new(IntegrityManifest.CurrentVersion + 1, "x", Hashes(files))), provider.PublicKey, Reader(files));
        Assert.Equal(IntegrityVerdict.Unverified, future.Verdict);
        Assert.Contains("versión", future.Detail);

        var empty = IntegrityManifest.Verify(Blob(provider.Key, new(IntegrityManifest.CurrentVersion, "x", new())), provider.PublicKey, Reader(files));
        Assert.Equal(IntegrityVerdict.Unverified, empty.Verdict);

        // Un nombre con carpeta en un manifiesto auténtico: error del proveedor, no se afirma nada (y nunca se compone la ruta).
        var traversal = IntegrityManifest.Verify(Blob(provider.Key, new(IntegrityManifest.CurrentVersion, "x",
            new() { [@"..\otra\cosa.dll"] = Hex(Bytes(1)) })), provider.PublicKey, Reader(files));
        Assert.Equal(IntegrityVerdict.Unverified, traversal.Verdict);
    }

    [Fact]
    public void Sign_RefusesWhatCouldNeverVerify()
    {
        using var provider = new Provider();
        Assert.Throws<ArgumentException>(() => provider.Sign(new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => provider.Sign(new Dictionary<string, string> { [@"sub\a.dll"] = Hex(Bytes(1)) }));
        Assert.Throws<ArgumentException>(() => provider.Sign(new Dictionary<string, string> { ["a.dll"] = "no-es-un-sha256" }));
    }

    [Fact]
    public void HashesCompareCaseInsensitively_AndTheSignedBytesAreTheOnesOnDisk()
    {
        using var provider = new Provider();
        var files = Install();
        string blob = provider.Sign(Hashes(files));

        // Un lector que devuelve el hexadecimal en MAYÚSCULAS sigue coincidiendo.
        Assert.Equal(IntegrityVerdict.Intact, IntegrityManifest.Verify(blob, provider.PublicKey, n => Reader(files)(n)?.ToUpperInvariant()).Verdict);

        // Lo que se verifica son los bytes EXACTOS del archivo: reformatear el JSON (aunque diga lo mismo) rompe la firma.
        int dot = blob.IndexOf('.');
        string pretty = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(blob[..dot])), new JsonSerializerOptions { WriteIndented = true });
        string reformatted = Convert.ToBase64String(Encoding.UTF8.GetBytes(pretty)) + blob[dot..];
        Assert.Equal(IntegrityVerdict.Unverified, IntegrityManifest.Verify(reformatted, provider.PublicKey, Reader(files)).Verdict);
    }
}
