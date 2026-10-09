using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Baioss.Record.Application.Licensing;
using Baioss.Record.Infrastructure.Licensing;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// El almacén de licencia y el servicio que decide Prueba / Licenciado / Caducado / Desconocido, probados DE VERDAD:
/// cada prueba usa un archivo y una rama HKCU PROPIOS (la copia HKLM no existe, como en una instalación sin
/// instalador) y una clave de proveedor de usar y tirar. Lo que no puede fallar en un grabador 24/7:
/// <list type="bullet">
/// <item>Un fallo NUESTRO nunca bloquea: Unknown no bloquea, y un hipo al leer no siembra una prueba nueva encima
/// del estado bueno.</item>
/// <item>El estado va atado a la huella, pero la CLAVE se rescata si la huella cambia (la migración de la huella v1
/// a la v2, o un chip TPM que no responde una sesión): prueba nueva, sin bloqueo, y la clave vuelve a valer en
/// cuanto la huella vuelve.</item>
/// <item>Borrar o manipular una copia no reinicia la prueba: manda la copia más estricta.</item>
/// <item>Al caducar solo se bloquean las grabaciones NUEVAS, y el motivo lleva el código de equipo.</item>
/// <item>Integridad de los binarios: un fallo NUESTRO al comprobarla (sin manifiesto, firma de otra clave, archivo
/// bloqueado, resumen no calculable, comprobación lenta) no cambia NADA; solo la evidencia POSITIVA de modificación
/// anula la licencia, y aun así mandan las reglas de la prueba (nunca se corta nada en curso).</item>
/// </list>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LicenseStoreServiceTests
{
    private static readonly byte[] MachineA = MachineFingerprintComposer.ComposeTpm(new byte[] { 1, 2, 3, 4 });
    private static readonly byte[] MachineB = MachineFingerprintComposer.Compose("guid", "vol", null, null, null);
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private sealed class FakeFingerprint(byte[] raw) : IMachineFingerprint
    {
        public ReadOnlyMemory<byte> Raw => raw;
        public string Code => MachineFingerprintComposer.ToCode(raw);
    }

    /// <summary>Emite licencias como la herramienta del proveedor, con una clave privada de usar y tirar.</summary>
    private sealed class Issuer : IDisposable
    {
        private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string PublicKey => Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());
        public string Issue(byte[] fingerprint, byte channels = 4)
        {
            var issuedOn = new DateOnly(2026, 10, 8);
            var message = LicenseKey.BuildSignedMessage(fingerprint, LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, channels);
            var signature = _ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return LicenseKey.Encode(LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, channels, signature);
        }
        /// <summary>El manifiesto de integridad se firma con la MISMA clave que las licencias, como en producción.</summary>
        public string SignManifest(IReadOnlyDictionary<string, string> hashes) => IntegrityManifest.Sign(hashes, _ecdsa, DateTimeOffset.UtcNow);
        public void Dispose() => _ecdsa.Dispose();
    }

    /// <summary>Una «instalación» de mentira: los cinco archivos sensibles con bytes aleatorios, en una carpeta propia,
    /// y su manifiesto si se pide. La autocomprobación REAL resume estos archivos del disco.</summary>
    private sealed class Install : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "baioss-license-tests", "install-" + Guid.NewGuid().ToString("N"));
        public string ManifestPath => Path.Combine(Dir, IntegrityManifest.FileName);

        public Install()
        {
            Directory.CreateDirectory(Dir);
            var rng = new Random(7);
            foreach (var name in IntegrityManifest.SensitiveFiles)
            {
                var bytes = new byte[4096];
                rng.NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(Dir, name), bytes);
            }
        }

        public Dictionary<string, string> Hashes() => IntegrityManifest.SensitiveFiles.ToDictionary(n => n, n =>
        {
            using var stream = File.OpenRead(Path.Combine(Dir, n));
            return IntegrityManifest.HashStream(stream);
        }, StringComparer.Ordinal);

        /// <summary>Firma el manifiesto de lo que hay AHORA en el disco.</summary>
        public void SignManifest(Issuer issuer) => File.WriteAllText(ManifestPath, issuer.SignManifest(Hashes()));

        /// <summary>«Parchea» un archivo: un byte más al final.</summary>
        public void Patch(string name)
        {
            using var stream = new FileStream(Path.Combine(Dir, name), FileMode.Append, FileAccess.Write, FileShare.None);
            stream.WriteByte(0x90);
        }

        public AssemblyIntegrity Check(string publicKey, Func<string, string?>? hashFile = null, TimeSpan? firstWait = null)
            => new(NullLogger.Instance, Dir, publicKey, hashFile, firstWait ?? TimeSpan.FromSeconds(10));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Archivo y rama del registro de ESTA prueba, borrados al terminar. Jamás los del producto.</summary>
    private sealed class Sandbox : IDisposable
    {
        private const string TestRoot = @"Software\Baioss\RecordTests";
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "baioss-license-tests", Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(Dir, "license.dat");
        public string RegistryPath { get; } = TestRoot + @"\" + Guid.NewGuid().ToString("N");

        public Sandbox() => Directory.CreateDirectory(Dir);

        public LicenseStore Store(byte[] fingerprint) => new(new FakeFingerprint(fingerprint), NullLogger.Instance, FilePath, RegistryPath);

        public LicenseService Service(byte[] fingerprint, string publicKey, AssemblyIntegrity? integrity = null)
            => new(new FakeFingerprint(fingerprint), Store(fingerprint), NullLogger<LicenseService>.Instance, publicKey, integrity);

        public string? UserRegistryValue()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue("State") as string;
        }

        public void SetUserRegistryValue(string blob)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("State", blob);
        }

        public void DeleteUserRegistryCopy() => Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);

        public void Dispose()
        {
            DeleteUserRegistryCopy();
            try
            {
                bool empty;
                using (var root = Registry.CurrentUser.OpenSubKey(TestRoot)) empty = root is { SubKeyCount: 0, ValueCount: 0 };
                if (empty) Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
            }
            catch { /* otra prueba en paralelo lo está usando: lo borrará la última */ }
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
            try
            {
                string parent = Path.GetDirectoryName(Dir)!;
                if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
            catch { /* otra prueba en paralelo lo está usando: lo borrará la última */ }
        }
    }

    private static LicenseRecord TrialStarted(DateTimeOffset at, long usedSeconds = 0, string? key = null) => new(at, at, usedSeconds, key);

    private static string RealStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Baioss", "Record", "license.dat");

    private static DateTime? RealStoreStamp() => File.Exists(RealStorePath) ? File.GetLastWriteTimeUtc(RealStorePath) : null;

    // --- Primer arranque ---

    [Fact]
    public void FirstRun_StartsTheTrial_InTheSandboxOnly()
    {
        var realBefore = RealStoreStamp();
        using var box = new Sandbox();
        using var issuer = new Issuer();
        using var svc = box.Service(MachineA, issuer.PublicKey);

        Assert.Equal(LicenseState.Trial, svc.Current.State);
        Assert.Equal(TrialPeriod.DefaultDays, svc.Current.DaysRemaining);
        Assert.False(svc.ShouldBlockNewRecordings);
        Assert.Null(svc.BlockReason);
        Assert.Equal(MachineFingerprintComposer.ToCode(MachineA), svc.MachineCode);
        Assert.True(File.Exists(box.FilePath));
        Assert.NotNull(box.UserRegistryValue());
        // GUARDA: estas pruebas no pueden tocar el estado real del equipo donde corren.
        Assert.Equal(realBefore, RealStoreStamp());
    }

    // --- Atado a la huella, con rescate de la clave ---

    [Fact]
    public void TheStoredState_OnlyValidatesUnderTheFingerprintThatWroteIt_AndTheKeyIsSalvaged()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        string key = issuer.Issue(MachineA);
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-20), key: key)));

        var sameMachine = box.Store(MachineA).Read();
        Assert.NotNull(sameMachine.Record);
        Assert.Equal(key, sameMachine.Record!.LicenseKey);
        Assert.False(sameMachine.HadReadErrors);
        Assert.Null(sameMachine.SalvagedKey);

        // Otra huella (otro equipo, o este con el TPM apagado): las marcas no valen, pero la clave se rescata.
        var otherMachine = box.Store(MachineB).Read();
        Assert.Null(otherMachine.Record);
        Assert.False(otherMachine.HadReadErrors);
        Assert.Equal(key, otherMachine.SalvagedKey);
    }

    [Fact]
    public void AFingerprintChange_RestartsTheTrialKeepingTheKey_AndTheKeyWorksAgainWhenTheFingerprintReturns()
    {
        // La migración de la huella v1 a la v2, o un chip TPM que no responde UNA sesión: el cliente licenciado ni
        // se queda bloqueado (prueba nueva) ni pierde su clave (vuelve a valer en cuanto la huella vuelve).
        using var box = new Sandbox();
        using var issuer = new Issuer();
        string key = issuer.Issue(MachineA);
        using (var licensed = box.Service(MachineA, issuer.PublicKey))
        {
            Assert.True(licensed.Activate(key).Success);
            Assert.Equal(LicenseState.Licensed, licensed.Current.State);
        }

        using (var changed = box.Service(MachineB, issuer.PublicKey))
        {
            Assert.Equal(LicenseState.Trial, changed.Current.State);
            Assert.Equal(TrialPeriod.DefaultDays, changed.Current.DaysRemaining);
            Assert.False(changed.ShouldBlockNewRecordings);
        }
        Assert.Equal(key, box.Store(MachineB).Read().Record?.LicenseKey); // reescrito bajo la huella nueva, CON la clave

        using var back = box.Service(MachineA, issuer.PublicKey);
        Assert.Equal(LicenseState.Licensed, back.Current.State);
        Assert.Equal(4, back.Current.LicensedChannels);
    }

    // --- Copias: borrar o manipular una no reinicia la prueba ---

    [Fact]
    public void DeletingOrTamperingACopy_DoesNotResetTheTrial()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        var expired = TrialStarted(Now.AddDays(-20));

        // Sin el archivo: queda la copia del registro del usuario.
        Assert.True(box.Store(MachineA).Write(expired));
        File.Delete(box.FilePath);
        using (var svc = box.Service(MachineA, issuer.PublicKey)) Assert.Equal(LicenseState.Expired, svc.Current.State);

        // Sin la copia del registro: queda el archivo.
        Assert.True(box.Store(MachineA).Write(expired));
        box.DeleteUserRegistryCopy();
        using (var svc = box.Service(MachineA, issuer.PublicKey)) Assert.Equal(LicenseState.Expired, svc.Current.State);

        // Archivo manipulado (fecha de inicio «de hoy» con el HMAC viejo): no cuenta; manda la copia del registro.
        Assert.True(box.Store(MachineA).Write(expired));
        string blob = File.ReadAllText(box.FilePath);
        int dot = blob.IndexOf('.');
        var record = JsonSerializer.Deserialize<LicenseRecord>(Encoding.UTF8.GetString(Convert.FromBase64String(blob[(dot + 1)..])))!;
        string forged = JsonSerializer.Serialize(record with { StartedAt = Now, HighWater = Now, UsedSeconds = 0 });
        File.WriteAllText(box.FilePath, blob[..dot] + "." + Convert.ToBase64String(Encoding.UTF8.GetBytes(forged)));
        using (var svc = box.Service(MachineA, issuer.PublicKey)) Assert.Equal(LicenseState.Expired, svc.Current.State);

        // Basura en las DOS copias y ninguna clave que rescatar: empieza una prueba nueva, sin excepciones.
        File.WriteAllText(box.FilePath, "esto no es un estado");
        box.SetUserRegistryValue("tampoco.esto");
        using (var svc = box.Service(MachineA, issuer.PublicKey)) Assert.Equal(LicenseState.Trial, svc.Current.State);
    }

    [Fact]
    public void TheStrictestCopyWins_WhenTheCopiesDisagree()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();

        // Archivo: prueba empezada hace 3 días. Registro del usuario: la misma prueba, pero empezada hace 20.
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-20))));
        string older = File.ReadAllText(box.FilePath);
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-3))));
        box.SetUserRegistryValue(older);
        using (var svc = box.Service(MachineA, issuer.PublicKey)) Assert.Equal(LicenseState.Expired, svc.Current.State);

        // Archivo: prueba de hoy. Registro: prueba de hoy, pero con 15 días de uso real acumulado.
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now, usedSeconds: 15 * 86_400)));
        string heavier = File.ReadAllText(box.FilePath);
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now)));
        box.SetUserRegistryValue(heavier);
        using (var svc = box.Service(MachineA, issuer.PublicKey)) Assert.Equal(LicenseState.Expired, svc.Current.State);
    }

    // --- Fallos nuestros: nunca bloquean ---

    [Fact]
    public void AnUnreadableStore_IsUnknown_NeverBlocks_AndIsNotTakenForAFirstRun()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        string key = issuer.Issue(MachineA);
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-20), key: key)));
        box.DeleteUserRegistryCopy(); // solo queda el archivo…
        string original = File.ReadAllText(box.FilePath);

        LicenseService svc;
        using (File.Open(box.FilePath, FileMode.Open, FileAccess.Read, FileShare.None)) // …y lo tiene bloqueado un antivirus
        {
            svc = box.Service(MachineA, issuer.PublicKey);
            Assert.Equal(LicenseState.Unknown, svc.Current.State);
            Assert.False(svc.ShouldBlockNewRecordings);
            Assert.True(svc.Current.CanStartRecording);
            Assert.True(svc.Current.NeedsAttention);
        }
        using (svc)
        {
            Assert.Equal(original, File.ReadAllText(box.FilePath)); // no se sembró una prueba nueva encima
            svc.RefreshForTests(TimeSpan.Zero);                      // el siguiente ciclo ya lee bien…
            Assert.Equal(LicenseState.Licensed, svc.Current.State);  // …y la clave guardada sigue ahí
        }
    }

    [Fact]
    public void WhenNeitherTheFileNorTheRegistryWork_NothingBlocks_AndActivateDoesNotClaimSuccess()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        // Archivo imposible (la ruta es un directorio) y rama del registro imposible (nombre de más de 255 caracteres).
        Directory.CreateDirectory(box.FilePath);
        var store = new LicenseStore(new FakeFingerprint(MachineA), NullLogger.Instance, box.FilePath, @"Software\Baioss\RecordTests\" + new string('x', 300));
        using var svc = new LicenseService(new FakeFingerprint(MachineA), store, NullLogger<LicenseService>.Instance, issuer.PublicKey);
        Assert.Equal(LicenseState.Unknown, svc.Current.State);
        Assert.False(svc.ShouldBlockNewRecordings);

        var result = svc.Activate(issuer.Issue(MachineA));
        Assert.False(result.Success);
        Assert.Equal(LicenseRejection.None, result.Rejection); // válida pero sin guardar: NO es «clave mala»
        Assert.Contains(box.FilePath, result.Message);
        Assert.False(svc.ShouldBlockNewRecordings);
    }

    // --- Caducidad ---

    [Fact]
    public void Expired_BlocksOnlyNewRecordings_AndNamesTheMachineCode()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-TrialPeriod.DefaultDays))));
        using var svc = box.Service(MachineA, issuer.PublicKey);

        Assert.Equal(LicenseState.Expired, svc.Current.State);
        Assert.Equal(0, svc.Current.DaysRemaining);
        Assert.True(svc.ShouldBlockNewRecordings);
        Assert.False(svc.Current.CanStartRecording);
        Assert.True(svc.Current.NeedsAttention);
        Assert.Contains(svc.MachineCode, svc.BlockReason);
    }

    [Fact]
    public void UsageIsMeasuredWithTheMonotonicClock_SoFreezingTheWallClockDoesNotFreezeTheTrial()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        var states = new List<LicenseState>();
        using (var svc = box.Service(MachineA, issuer.PublicKey))
        {
            svc.Changed += (_, info) => states.Add(info.State);
            Assert.Equal(LicenseState.Trial, svc.Current.State);
            // 15 días de uso real medidos con el reloj monotónico, con el reloj de pared «parado».
            svc.RefreshForTests(TimeSpan.FromDays(15));
            Assert.Equal(LicenseState.Expired, svc.Current.State);
            Assert.True(svc.ShouldBlockNewRecordings);
            Assert.Contains(LicenseState.Expired, states); // la UI se entera
        }
        // Y quedó persistido: un servicio nuevo lo ve caducado.
        using var again = box.Service(MachineA, issuer.PublicKey);
        Assert.Equal(LicenseState.Expired, again.Current.State);
    }

    // --- Activación ---

    [Fact]
    public void Activate_RejectsAKeyForAnotherMachine_OrGarbage_AndChangesNothing()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        using var svc = box.Service(MachineA, issuer.PublicKey);

        var other = svc.Activate(issuer.Issue(MachineB));
        Assert.False(other.Success);
        Assert.Equal(LicenseRejection.OtherMachine, other.Rejection);
        Assert.Equal(LicenseState.Trial, svc.Current.State);
        Assert.Null(box.Store(MachineA).Read().Record?.LicenseKey);

        var garbage = svc.Activate("esto no es una licencia");
        Assert.False(garbage.Success);
        Assert.Equal(LicenseRejection.Malformed, garbage.Rejection);
        Assert.Equal(LicenseState.Trial, svc.Current.State);
    }

    [Fact]
    public void Activate_PersistsAValidKey_AndAFreshServiceComesUpLicensed_WithTheSignedChannels()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        var states = new List<LicenseState>();
        using (var svc = box.Service(MachineA, issuer.PublicKey))
        {
            svc.Changed += (_, info) => states.Add(info.State);
            var result = svc.Activate(issuer.Issue(MachineA, channels: 2));
            Assert.True(result.Success);
            Assert.Equal(LicenseRejection.None, result.Rejection);
            Assert.Equal(LicenseState.Licensed, svc.Current.State);
            Assert.Equal(2, svc.Current.LicensedChannels);
            Assert.False(svc.ShouldBlockNewRecordings);
            Assert.Contains(LicenseState.Licensed, states);
        }
        Assert.NotNull(box.UserRegistryValue());

        using var fresh = box.Service(MachineA, issuer.PublicKey);
        Assert.Equal(LicenseState.Licensed, fresh.Current.State);
        Assert.Equal(2, fresh.Current.LicensedChannels);

        // Una clave firmada por OTRO emisor (alguien fabricando licencias) no activa nada ni estropea lo activado.
        using var forger = new Issuer();
        Assert.NotEqual(LicenseRejection.None, fresh.Activate(forger.Issue(MachineA)).Rejection);
        Assert.Equal(LicenseState.Licensed, fresh.Current.State);
    }

    [Fact]
    public void ActivatingOnAnExpiredMachine_UnblocksRecordingAtOnce()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-30))));
        using var svc = box.Service(MachineA, issuer.PublicKey);
        Assert.True(svc.ShouldBlockNewRecordings);

        Assert.True(svc.Activate(issuer.Issue(MachineA)).Success);
        Assert.Equal(LicenseState.Licensed, svc.Current.State);
        Assert.False(svc.ShouldBlockNewRecordings);
        Assert.Null(svc.BlockReason);
    }

    // --- Integridad de los binarios: un fallo nuestro no cambia nada; la evidencia positiva anula la licencia ---

    [Fact]
    public void WithAnIntactInstall_TheLicenseAppliesAsAlways()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        using var install = new Install();
        install.SignManifest(issuer);
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-30), key: issuer.Issue(MachineA, channels: 2))));

        using var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey));

        Assert.Equal(LicenseState.Licensed, svc.Current.State);
        Assert.Equal(IntegrityVerdict.Intact, svc.Current.Integrity);
        Assert.Equal(2, svc.Current.LicensedChannels);
        Assert.False(svc.ShouldBlockNewRecordings);
        Assert.False(svc.Current.NeedsAttention);
        Assert.DoesNotContain("modificada", svc.Current.Summary);
    }

    [Fact]
    public void AGenuineFailureToCheckIntegrity_ChangesNothing()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        // Prueba caducada hace tiempo: SOLO la clave mantiene el equipo grabando. Si algún fallo nuestro al comprobar
        // la integridad anulara la clave, aquí se vería como un bloqueo.
        string key = issuer.Issue(MachineA);
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-30), key: key)));

        // Sin manifiesto (desarrollo, copia portable).
        using (var install = new Install())
        using (var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey)))
        {
            Assert.Equal(LicenseState.Licensed, svc.Current.State);
            Assert.Equal(IntegrityVerdict.Unverified, svc.Current.Integrity);
            Assert.Contains("no hay manifiesto", svc.Current.IntegrityDetail);
            Assert.False(svc.ShouldBlockNewRecordings);
        }

        // Manifiesto firmado con OTRA clave (error de empaquetado, o un manifiesto fabricado): tampoco se afirma nada.
        using (var install = new Install())
        {
            using var other = new Issuer();
            install.SignManifest(other);
            using var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey));
            Assert.Equal(LicenseState.Licensed, svc.Current.State);
            Assert.Equal(IntegrityVerdict.Unverified, svc.Current.Integrity);
            Assert.False(svc.ShouldBlockNewRecordings);
        }

        // Un ensamblado bloqueado en exclusiva (antivirus) justo al comprobar.
        using (var install = new Install())
        {
            install.SignManifest(issuer);
            using var lockHandle = new FileStream(Path.Combine(install.Dir, "Baioss.Record.Infrastructure.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
            using var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey));
            Assert.Equal(LicenseState.Licensed, svc.Current.State);
            Assert.Equal(IntegrityVerdict.Unverified, svc.Current.Integrity);
            Assert.False(svc.ShouldBlockNewRecordings);
        }

        // El resumen no se puede calcular (un disco que falla al leer).
        using (var install = new Install())
        {
            install.SignManifest(issuer);
            using var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey, hashFile: _ => throw new IOException("E/S")));
            Assert.Equal(LicenseState.Licensed, svc.Current.State);
            Assert.Equal(IntegrityVerdict.Unverified, svc.Current.Integrity);
            Assert.False(svc.ShouldBlockNewRecordings);
        }

        // Y sin autocomprobación registrada en absoluto (como hasta ahora).
        using (var svc = box.Service(MachineA, issuer.PublicKey))
        {
            Assert.Equal(LicenseState.Licensed, svc.Current.State);
            Assert.Equal(IntegrityVerdict.Unverified, svc.Current.Integrity);
        }
    }

    [Fact]
    public void PositiveEvidenceOfTampering_VoidsTheLicense_AndTheTrialRulesApply()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        using var install = new Install();
        install.SignManifest(issuer);
        install.Patch("Baioss.Record.Infrastructure.dll");
        string key = issuer.Issue(MachineA);

        // Prueba en curso: la clave NO se aplica, pero la prueba sigue → no se bloquea; se avisa para reinstalar a tiempo.
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-3), key: key)));
        using (var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey)))
        {
            Assert.Equal(LicenseState.Trial, svc.Current.State);
            Assert.Equal(IntegrityVerdict.Tampered, svc.Current.Integrity);
            Assert.True(svc.Current.IsTampered);
            Assert.Null(svc.Current.LicensedChannels);
            Assert.Contains("Baioss.Record.Infrastructure.dll", svc.Current.IntegrityDetail);
            Assert.False(svc.ShouldBlockNewRecordings);
            Assert.True(svc.Current.CanStartRecording);
            Assert.True(svc.Current.NeedsAttention);
            Assert.Contains("instalación modificada", svc.Current.Summary);
            Assert.Equal(key, box.Store(MachineA).Read().Record?.LicenseKey); // la clave no se pierde
        }

        // Prueba terminada: se trata como NO licenciada → bloquea grabaciones nuevas, y el motivo dice la verdad y la salida.
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-30), key: key)));
        using (var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey)))
        {
            Assert.Equal(LicenseState.Expired, svc.Current.State);
            Assert.True(svc.ShouldBlockNewRecordings);
            Assert.False(svc.Current.CanStartRecording);
            Assert.Contains("no coincide", svc.BlockReason);
            Assert.Contains("reinstala", svc.BlockReason);
            Assert.Contains(svc.MachineCode, svc.BlockReason);
        }

        // Reinstalada (los archivos vuelven a coincidir con un manifiesto firmado): la misma clave vuelve a valer sin tocar nada.
        install.SignManifest(issuer);
        using (var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey)))
        {
            Assert.Equal(LicenseState.Licensed, svc.Current.State);
            Assert.Equal(IntegrityVerdict.Intact, svc.Current.Integrity);
            Assert.False(svc.ShouldBlockNewRecordings);
            Assert.Null(svc.BlockReason);
        }
    }

    [Fact]
    public void TamperingPlusAnUnreadableStore_IsUnknown_AndDoesNotBlock()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        using var install = new Install();
        install.SignManifest(issuer);
        install.Patch("Baioss.Record.App.dll");
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-30))));
        box.DeleteUserRegistryCopy();

        using (File.Open(box.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey)))
        {
            Assert.Equal(LicenseState.Unknown, svc.Current.State);          // sin certeza de que la prueba terminó…
            Assert.Equal(IntegrityVerdict.Tampered, svc.Current.Integrity); // …aunque la evidencia se informe…
            Assert.False(svc.ShouldBlockNewRecordings);                     // …no hay nada que bloquear
            Assert.True(svc.Current.CanStartRecording);
        }
    }

    [Fact]
    public void ActivatingOnATamperedInstall_SavesTheKey_ButDoesNotClaimSuccess()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        using var install = new Install();
        install.SignManifest(issuer);
        install.Patch("Baioss.Record.App.exe");
        string key = issuer.Issue(MachineA, channels: 3);

        using (var svc = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey)))
        {
            var result = svc.Activate(key);
            Assert.False(result.Success);
            Assert.Equal(LicenseRejection.None, result.Rejection); // no es «clave mala»: válida pero no aplicable
            Assert.Contains("reinstala", result.Message);
            Assert.Equal(LicenseState.Trial, svc.Current.State);
            Assert.Null(svc.Current.LicensedChannels);
        }
        Assert.Equal(key, box.Store(MachineA).Read().Record?.LicenseKey); // guardada: sobrevive a la reinstalación

        install.SignManifest(issuer); // la instalación vuelve a coincidir
        using var fresh = box.Service(MachineA, issuer.PublicKey, install.Check(issuer.PublicKey));
        Assert.Equal(LicenseState.Licensed, fresh.Current.State);
        Assert.Equal(3, fresh.Current.LicensedChannels);
    }

    [Fact]
    public void ASlowIntegrityCheck_NeverHoldsTheServiceBeyondItsBudget_AndIsAppliedInTheNextCycle()
    {
        using var box = new Sandbox();
        using var issuer = new Issuer();
        using var install = new Install();
        install.SignManifest(issuer);
        install.Patch("Baioss.Record.Application.dll");
        Assert.True(box.Store(MachineA).Write(TrialStarted(Now.AddDays(-30), key: issuer.Issue(MachineA))));
        using var disk = new ManualResetEventSlim(false); // «el disco no responde»
        var hashes = install.Hashes();
        var check = install.Check(issuer.PublicKey, hashFile: n => { disk.Wait(); return hashes.GetValueOrDefault(n); },
            firstWait: TimeSpan.FromMilliseconds(300));

        var sw = Stopwatch.StartNew();
        using var svc = box.Service(MachineA, issuer.PublicKey, check);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"el servicio tardó {sw.Elapsed} en arrancar");
        Assert.Equal(LicenseState.Licensed, svc.Current.State);           // sin evidencia todavía, la clave vale
        Assert.Equal(IntegrityVerdict.Unverified, svc.Current.Integrity);
        Assert.False(svc.ShouldBlockNewRecordings);

        var states = new List<LicenseState>();
        svc.Changed += (_, info) => states.Add(info.State);
        disk.Set(); // el disco responde; el siguiente ciclo recoge el veredicto
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (svc.Current.Integrity != IntegrityVerdict.Tampered && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            svc.RefreshForTests(TimeSpan.Zero);
        }
        Assert.Equal(IntegrityVerdict.Tampered, svc.Current.Integrity);
        Assert.Equal(LicenseState.Expired, svc.Current.State);
        Assert.True(svc.ShouldBlockNewRecordings);
        Assert.Contains(LicenseState.Expired, states); // la UI se entera
    }
}
