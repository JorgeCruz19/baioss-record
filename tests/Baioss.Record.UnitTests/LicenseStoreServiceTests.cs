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
        public void Dispose() => _ecdsa.Dispose();
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

        public LicenseService Service(byte[] fingerprint, string publicKey)
            => new(new FakeFingerprint(fingerprint), Store(fingerprint), NullLogger<LicenseService>.Instance, publicKey);

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
}
