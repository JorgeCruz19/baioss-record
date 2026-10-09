using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Application.Licensing;
using Baioss.Record.Infrastructure.Licensing;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Huella v2 en dos niveles: anclada al chip TPM (la clave de endoso, que una copia del disco no se lleva) o clásica
/// (equipos sin TPM). Lo que no puede fallar: que los niveles nunca se confundan, que el nivel viaje en el código de
/// equipo (el proveedor lo ve al emitir), que una licencia emitida para un chip no valga en otro, y que las respuestas
/// raras del chip o del firmware se traten como «no hay», nunca como un identificador.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class MachineFingerprintTpmTests
{
    private static byte[] FakeEndorsementKey(int seed)
    {
        var area = new byte[314]; // lo que mide el área pública de una EK RSA-2048
        new Random(seed).NextBytes(area);
        area[0] = 0x00; area[1] = 0x01; // TPM_ALG_RSA
        return area;
    }

    // --- Compositor ---

    [Fact]
    public void TheSameChip_AlwaysGivesTheSameFingerprint_AndAnotherChipADifferentOne()
    {
        var a1 = MachineFingerprintComposer.ComposeTpm(FakeEndorsementKey(1));
        var a2 = MachineFingerprintComposer.ComposeTpm(FakeEndorsementKey(1));
        var b = MachineFingerprintComposer.ComposeTpm(FakeEndorsementKey(2));
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.Equal(MachineFingerprintComposer.Length, a1.Length);
    }

    [Fact]
    public void TheTier_TravelsInTheFingerprint_AndInTheMachineCode()
    {
        // 200 huellas de cada nivel: el bit de nivel es fijo, no depende del hash.
        for (int i = 0; i < 200; i++)
        {
            var tpm = MachineFingerprintComposer.ComposeTpm(FakeEndorsementKey(i));
            var classic = MachineFingerprintComposer.Compose($"guid-{i}", $"vol-{i}", null, null, null);
            Assert.True(MachineFingerprintComposer.IsTpmAnchored(tpm));
            Assert.False(MachineFingerprintComposer.IsTpmAnchored(classic));
            Assert.True(MachineFingerprintComposer.IsTpmAnchoredCode(MachineFingerprintComposer.ToCode(tpm)));
            Assert.False(MachineFingerprintComposer.IsTpmAnchoredCode(MachineFingerprintComposer.ToCode(classic)));
        }
        Assert.Null(MachineFingerprintComposer.IsTpmAnchoredCode("esto no es un código"));
        Assert.Null(MachineFingerprintComposer.IsTpmAnchoredCode(null));
        Assert.False(MachineFingerprintComposer.IsTpmAnchored(new byte[3])); // longitud inesperada
    }

    [Fact]
    public void AnEmptyEndorsementKey_CanNeverBecomeAFingerprint()
        // Sería la misma huella en cualquier equipo: una licencia universal.
        => Assert.Throws<ArgumentException>(() => MachineFingerprintComposer.ComposeTpm(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void TheGenericFingerprint_IsOnlyTheClassicOneWithNoIdentifiers()
    {
        Assert.True(MachineFingerprintComposer.IsGeneric(MachineFingerprintComposer.Compose(null, null, null, null, null)));
        Assert.True(MachineFingerprintComposer.IsGeneric(MachineFingerprintComposer.Compose("", "N/A", "Default string", "To be filled by O.E.M.", "00000000000000000000000000000000")));
        Assert.False(MachineFingerprintComposer.IsGeneric(MachineFingerprintComposer.Compose("guid", null, null, null, null)));
        Assert.False(MachineFingerprintComposer.IsGeneric(MachineFingerprintComposer.ComposeTpm(FakeEndorsementKey(7))));
    }

    [Theory]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    [InlineData("ffffffffffffffffffffffffffffffff")]
    [InlineData("00020003000400050006000700080009")] // la secuencia de ejemplo que dejan muchas placas genéricas
    public void PlaceholderSystemUuids_AreTreatedAsAbsent(string junk)
    {
        Assert.Equal("", MachineFingerprintComposer.Normalize(junk));
        Assert.Equal(
            MachineFingerprintComposer.Compose("guid", "vol", null, null, junk),
            MachineFingerprintComposer.Compose("guid", "vol", null, null, null));
    }

    [Fact]
    public void ARealSystemUuid_ChangesTheClassicFingerprint()
        // Dos equipos de integrador con el disco clonado (mismo MachineGuid y volumen) pero UUID de firmware distintos.
        => Assert.NotEqual(
            MachineFingerprintComposer.Compose("guid", "vol", null, null, "79AEF9F91E1F45CE84BD1DCEC8960A8C"),
            MachineFingerprintComposer.Compose("guid", "vol", null, null, "11111111222233334444555555555555"));

    // --- Licencia: el chip es lo que la ata ---

    [Fact]
    public void ALicenseIssuedForOneChip_IsRejectedOnAnother_AndOnTheSameDiskWithoutTheChip()
    {
        using var vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(vendor.ExportSubjectPublicKeyInfo());
        var original = MachineFingerprintComposer.ComposeTpm(FakeEndorsementKey(10));
        var issuedOn = new DateOnly(2026, 10, 3);
        var signature = vendor.SignData(
            LicenseKey.BuildSignedMessage(original, LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, 4),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string key = LicenseKey.Encode(LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, 4, signature);

        Assert.Equal(LicenseRejection.None, LicenseKey.Verify(key, original, publicKey));
        // El clon: mismo disco (mismos MachineGuid y volumen), otro chip…
        Assert.Equal(LicenseRejection.OtherMachine,
            LicenseKey.Verify(key, MachineFingerprintComposer.ComposeTpm(FakeEndorsementKey(11)), publicKey));
        // …o el TPM apagado en el clon para forzar la huella clásica.
        Assert.Equal(LicenseRejection.OtherMachine,
            LicenseKey.Verify(key, MachineFingerprintComposer.Compose("guid", "vol", null, null, null), publicKey));
    }

    // --- Respuesta del chip (TPM2_ReadPublic) ---

    private static byte[] ReadPublicResponse(byte[] publicArea, uint responseCode = 0)
    {
        if (responseCode != 0)
        {
            var error = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(error, 0x8001);
            BinaryPrimitives.WriteUInt32BigEndian(error.AsSpan(2), 10);
            BinaryPrimitives.WriteUInt32BigEndian(error.AsSpan(6), responseCode);
            return error;
        }
        var name = new byte[34]; // TPM2B_NAME: algoritmo + SHA-256 (su contenido no importa aquí)
        var response = new byte[10 + 2 + publicArea.Length + 2 + name.Length + 2 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x8001);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(2), (uint)response.Length);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(10), (ushort)publicArea.Length);
        publicArea.CopyTo(response, 12);
        int o = 12 + publicArea.Length;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(o), (ushort)name.Length); o += 2 + name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(o), (ushort)name.Length);
        return response;
    }

    [Fact]
    public void AGoodChipResponse_YieldsExactlyThePublicArea()
    {
        var ek = FakeEndorsementKey(3);
        var area = TpmEndorsementKey.ParseReadPublicResponse(ReadPublicResponse(ek), out string detail);
        Assert.Equal(ek, area);
        Assert.Contains("RSA", detail);
    }

    [Fact]
    public void ChipErrorsAndMalformedResponses_AreNeverTakenForAKey()
    {
        var ek = FakeEndorsementKey(4);
        // El chip dice que ese identificador no existe (TPM_RC_HANDLE).
        Assert.Null(TpmEndorsementKey.ParseReadPublicResponse(ReadPublicResponse(ek, responseCode: 0x18B), out string missing));
        Assert.Contains("0x18B", missing);
        // Cortada, vacía, con el tamaño declarado distinto del real, o con un área absurda.
        var good = ReadPublicResponse(ek);
        Assert.Null(TpmEndorsementKey.ParseReadPublicResponse(good.AsSpan(0, 5), out _));
        Assert.Null(TpmEndorsementKey.ParseReadPublicResponse(ReadOnlySpan<byte>.Empty, out _));
        Assert.Null(TpmEndorsementKey.ParseReadPublicResponse(good.AsSpan(0, good.Length - 1), out _));
        var tooBig = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(tooBig.AsSpan(10), 0xFFFF);
        Assert.Null(TpmEndorsementKey.ParseReadPublicResponse(tooBig, out _));
        Assert.Null(TpmEndorsementKey.ParseReadPublicResponse(ReadPublicResponse(new byte[8]), out _)); // «clave» de 8 bytes
        var unknownType = FakeEndorsementKey(5); unknownType[1] = 0x99;
        Assert.Null(TpmEndorsementKey.ParseReadPublicResponse(ReadPublicResponse(unknownType), out _));
    }

    // --- UUID de sistema del SMBIOS ---

    private static byte[] SmbiosTable(byte[]? uuid, bool withStrings = true)
    {
        var table = new List<byte>(new byte[8]); // cabecera de Windows (su contenido no se mira)
        // Tipo 0 (BIOS), 0x18 bytes, con dos cadenas.
        var bios = new byte[0x18]; bios[0] = 0; bios[1] = 0x18;
        table.AddRange(bios);
        table.AddRange("Fabricante\0Version 1.0\0\0"u8.ToArray());
        if (uuid is not null)
        {
            var system = new byte[0x1B]; system[0] = 1; system[1] = 0x1B;
            uuid.CopyTo(system, 8);
            table.AddRange(system);
            table.AddRange(withStrings ? "ACME\0Modelo X\0SN123\0\0"u8.ToArray() : new byte[] { 0, 0 });
        }
        table.AddRange(new byte[] { 127, 4, 0, 0, 0, 0 }); // fin de tabla
        return table.ToArray();
    }

    [Fact]
    public void TheSystemUuid_IsReadFromTheType1Structure_WithOrWithoutStrings()
    {
        var uuid = Convert.FromHexString("79AEF9F91E1F45CE84BD1DCEC8960A8C");
        Assert.Equal("79AEF9F91E1F45CE84BD1DCEC8960A8C", SmbiosSystemUuid.Parse(SmbiosTable(uuid)));
        Assert.Equal("79AEF9F91E1F45CE84BD1DCEC8960A8C", SmbiosSystemUuid.Parse(SmbiosTable(uuid, withStrings: false)));
    }

    [Fact]
    public void ATableWithoutSystemInformation_OrCutShort_GivesNoUuid()
    {
        Assert.Null(SmbiosSystemUuid.Parse(SmbiosTable(uuid: null)));
        Assert.Null(SmbiosSystemUuid.Parse(ReadOnlySpan<byte>.Empty));
        var cut = SmbiosTable(Convert.FromHexString("79AEF9F91E1F45CE84BD1DCEC8960A8C"));
        Assert.Null(SmbiosSystemUuid.Parse(cut.AsSpan(0, 8 + 0x18 + 24 + 10))); // la estructura de tipo 1, a medias
    }

    // --- En este equipo (con o sin TPM): la huella real es estable y dice la verdad sobre su nivel ---

    [Fact]
    public void OnThisMachine_TheFingerprintIsStable_AndItsTierMatchesItsCode()
    {
        var first = new WindowsMachineFingerprint(NullLogger<WindowsMachineFingerprint>.Instance);
        var second = new WindowsMachineFingerprint(NullLogger<WindowsMachineFingerprint>.Instance);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.Raw.ToArray(), second.Raw.ToArray());
        Assert.Equal(first.TpmAnchored, MachineFingerprintComposer.IsTpmAnchored(first.Raw.Span));
        Assert.Equal(first.TpmAnchored, MachineFingerprintComposer.IsTpmAnchoredCode(first.Code));
        Assert.False(MachineFingerprintComposer.IsGeneric(first.Raw.Span));
    }

    [Fact]
    public void ReadingTheChip_NeverThrows_AndIsConsistent()
    {
        var a = TpmEndorsementKey.TryRead(_ => { });
        var b = TpmEndorsementKey.TryRead(_ => { });
        Assert.Equal(a.PublicArea is null, b.PublicArea is null);
        if (a.PublicArea is not null) Assert.Equal(a.PublicArea, b.PublicArea);
        Assert.False(string.IsNullOrWhiteSpace(a.Detail));
    }

    // --- Los DOS niveles del lector real, con un chip simulado (el equipo de pruebas solo tiene uno de los dos) ---

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static TpmEndorsementKey.ReadResult NoTpm() => new(null, "el equipo no tiene un TPM 2.0 activo");

    [Fact]
    public void WithAChip_TheFingerprintIsOnlyTheChips_AndTheLogSaysSo()
    {
        var ek = FakeEndorsementKey(21);
        var log = new CapturingLogger<WindowsMachineFingerprint>();
        var fp = new WindowsMachineFingerprint(log, () => new TpmEndorsementKey.ReadResult(ek, "clave de endoso RSA leída del chip"));

        Assert.True(fp.TpmAnchored);
        Assert.Equal(MachineFingerprintComposer.ComposeTpm(ek), fp.Raw.ToArray());
        Assert.Equal(MachineFingerprintComposer.ToCode(fp.Raw.Span), fp.Code);
        Assert.True(MachineFingerprintComposer.IsTpmAnchoredCode(fp.Code));
        Assert.Contains(log.Lines, l => l.Contains("anclada al chip TPM") && l.Contains(fp.Code));
    }

    [Fact]
    public void WithoutAChip_TheClassicFingerprintIsBuilt_FromThisMachinesIdentifiers()
    {
        // El camino que recorren los equipos sin TPM, ejercitado aquí aunque este equipo sí lo tenga.
        var log = new CapturingLogger<WindowsMachineFingerprint>();
        var first = new WindowsMachineFingerprint(log, NoTpm);
        var second = new WindowsMachineFingerprint(NullLogger<WindowsMachineFingerprint>.Instance, NoTpm);
        var withChip = new WindowsMachineFingerprint(NullLogger<WindowsMachineFingerprint>.Instance,
            () => new TpmEndorsementKey.ReadResult(FakeEndorsementKey(1), "ok"));

        Assert.False(first.TpmAnchored);
        Assert.False(MachineFingerprintComposer.IsTpmAnchored(first.Raw.Span));
        Assert.False(MachineFingerprintComposer.IsTpmAnchoredCode(first.Code));
        Assert.Equal(first.Code, second.Code);                              // estable entre lecturas
        Assert.Equal(first.Raw.ToArray(), second.Raw.ToArray());
        Assert.NotEqual(first.Raw.ToArray(), withChip.Raw.ToArray());       // y distinta de la anclada al chip
        Assert.False(MachineFingerprintComposer.IsGeneric(first.Raw.Span)); // este equipo tiene al menos MachineGuid
        // El registro dice que es clásica, por qué no hubo TPM y qué componentes participaron; sin errores.
        string line = Assert.Single(log.Lines, l => l.Contains("clásica"));
        Assert.Contains("no tiene un TPM 2.0 activo", line);
        Assert.Contains("MachineGuid", line);
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("Error"));
    }

    [Fact]
    public void AReaderThatThrows_FallsBackToTheClassicTier_InsteadOfBreakingStartup()
    {
        var fp = new WindowsMachineFingerprint(NullLogger<WindowsMachineFingerprint>.Instance, () => throw new InvalidOperationException("boom"));
        Assert.False(fp.TpmAnchored);
        Assert.Equal(MachineFingerprintComposer.Length, fp.Raw.Length);
        Assert.Equal(fp.Raw.ToArray(), new WindowsMachineFingerprint(NullLogger<WindowsMachineFingerprint>.Instance, NoTpm).Raw.ToArray());
    }

    // --- Reintentos: solo los fallos PASAJEROS se reintentan, y nunca más de ≈ 3,75 s ---

    private static (TpmEndorsementKey.ReadResult, bool) Failure(string why, bool definitive) => (new TpmEndorsementKey.ReadResult(null, why), definitive);
    private static (TpmEndorsementKey.ReadResult, bool) Success(byte[] ek) => (new TpmEndorsementKey.ReadResult(ek, "ok"), true);

    [Fact]
    public void ADefinitiveFailure_IsNotRetried()
    {
        int calls = 0; var sleeps = new List<int>();
        var r = TpmEndorsementKey.TryRead(() => { calls++; return Failure("el equipo no tiene un TPM 2.0 activo", definitive: true); }, sleeps.Add);
        Assert.Null(r.PublicArea);
        Assert.Equal(1, calls);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void ATransientFailure_IsRetriedWithGrowingWaits_UntilItSucceeds()
    {
        var ek = FakeEndorsementKey(30);
        int calls = 0; var sleeps = new List<int>();
        var r = TpmEndorsementKey.TryRead(
            () => ++calls < 3 ? Failure("el servicio base de TPM no respondió (0x8028400B)", definitive: false) : Success(ek),
            sleeps.Add);
        Assert.Equal(ek, r.PublicArea);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { 250, 500 }, sleeps);
    }

    [Fact]
    public void APersistentTransientFailure_GivesUpAfterTheBudget_WithTheLastReason()
    {
        int calls = 0; var sleeps = new List<int>();
        var r = TpmEndorsementKey.TryRead(() => { calls++; return Failure($"intento {calls}", definitive: false); }, sleeps.Add);
        Assert.Null(r.PublicArea);
        Assert.Equal(TpmEndorsementKey.RetryDelaysMs.Length + 1, calls);
        Assert.Equal(TpmEndorsementKey.RetryDelaysMs, sleeps);
        Assert.Equal(3750, sleeps.Sum()); // el presupuesto: decidir el nivel no puede retrasar el arranque mucho más
        Assert.Equal($"intento {calls}", r.Detail);
    }

    [Fact]
    public void AMissingTbsLibrary_IsDefinitive_ButAnyOtherExceptionIsTransient()
    {
        int calls = 0; var sleeps = new List<int>();
        var r = TpmEndorsementKey.TryRead(() => { calls++; throw new DllNotFoundException("tbs.dll"); }, sleeps.Add);
        Assert.Null(r.PublicArea);
        Assert.Contains("tbs.dll", r.Detail);
        Assert.Equal(1, calls);
        Assert.Empty(sleeps);

        var ek = FakeEndorsementKey(31);
        calls = 0; sleeps.Clear();
        var r2 = TpmEndorsementKey.TryRead(() => ++calls < 3 ? throw new InvalidOperationException("hipo") : Success(ek), sleeps.Add);
        Assert.Equal(ek, r2.PublicArea);
        Assert.Equal(new[] { 250, 500 }, sleeps);
    }

    [Fact]
    public void ASuccessOnTheFirstTry_NeverSleeps()
    {
        var sleeps = new List<int>();
        var r = TpmEndorsementKey.TryRead(() => Success(FakeEndorsementKey(32)), sleeps.Add);
        Assert.NotNull(r.PublicArea);
        Assert.Empty(sleeps);
    }

    // --- SMBIOS: tablas viejas o corruptas ---

    [Fact]
    public void AnOldSmbiosWithoutUuid_OrACorruptStructure_GivesNoUuid_WithoutCrashing()
    {
        var uuid = Convert.FromHexString("79AEF9F91E1F45CE84BD1DCEC8960A8C");

        // Tipo 1 de SMBIOS 2.0 (8 bytes): aún no tenía UUID.
        var old = new List<byte>(new byte[8]);
        old.AddRange(new byte[] { 1, 0x08, 0, 0, 0, 0, 0, 0 }); old.AddRange(new byte[] { 0, 0 });
        old.AddRange(new byte[] { 127, 4, 0, 0, 0, 0 });
        Assert.Null(SmbiosSystemUuid.Parse(old.ToArray()));

        // Una estructura con longitud corrupta (< 4) delante del tipo 1: se abandona, no se lee basura.
        var corrupt = new List<byte>(new byte[8]);
        corrupt.AddRange(new byte[] { 0, 2, 0, 0 }); corrupt.AddRange(new byte[] { 0, 0 });
        corrupt.AddRange(SmbiosTable(uuid)[8..]);
        Assert.Null(SmbiosSystemUuid.Parse(corrupt.ToArray()));

        // El tipo 1 como PRIMERA estructura (sin tipo 0 delante) se lee igual.
        var first = new List<byte>(new byte[8]);
        var system = new byte[0x1B]; system[0] = 1; system[1] = 0x1B; uuid.CopyTo(system, 8);
        first.AddRange(system); first.AddRange(new byte[] { 0, 0 });
        Assert.Equal("79AEF9F91E1F45CE84BD1DCEC8960A8C", SmbiosSystemUuid.Parse(first.ToArray()));
    }
}
