using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Application.Licensing;
using Baioss.Record.Infrastructure.Licensing;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// La autocomprobación REAL (archivos en disco, manifiesto junto a ellos, resúmenes calculados de verdad), en una
/// carpeta propia de cada prueba. Lo que no puede fallar: que nunca lance ni retenga el arranque más de su plazo,
/// que sin manifiesto o con uno inutilizable no afirme nada, y que un archivo parcheado se detecte y se nombre.
/// </summary>
public sealed class AssemblyIntegrityTests
{
    /// <summary>Una «instalación» de mentira: los cinco archivos sensibles con bytes aleatorios.</summary>
    private sealed class Install : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "baioss-integrity-tests", Guid.NewGuid().ToString("N"));
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
        public string ManifestPath => Path.Combine(Dir, IntegrityManifest.FileName);

        public Install()
        {
            Directory.CreateDirectory(Dir);
            var rng = new Random(42);
            foreach (var name in IntegrityManifest.SensitiveFiles)
            {
                var bytes = new byte[8192];
                rng.NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(Dir, name), bytes);
            }
        }

        public Dictionary<string, string> Hashes() => IntegrityManifest.SensitiveFiles.ToDictionary(n => n, n =>
        {
            using var stream = File.OpenRead(Path.Combine(Dir, n));
            return IntegrityManifest.HashStream(stream);
        }, StringComparer.Ordinal);

        /// <summary>Firma el manifiesto de lo que hay AHORA en el disco (con esta clave, o con otra).</summary>
        public void SignManifest(ECDsa? with = null)
            => File.WriteAllText(ManifestPath, IntegrityManifest.Sign(Hashes(), with ?? Key, DateTimeOffset.UtcNow));

        /// <summary>«Parchea» un archivo: un byte más al final.</summary>
        public void Patch(string name)
        {
            using var stream = new FileStream(Path.Combine(Dir, name), FileMode.Append, FileAccess.Write, FileShare.None);
            stream.WriteByte(0x90);
        }

        public AssemblyIntegrity Check(Func<string, string?>? hashFile = null, TimeSpan? firstWait = null)
            => new(NullLogger.Instance, Dir, PublicKey, hashFile, firstWait ?? TimeSpan.FromSeconds(10));

        public void Dispose()
        {
            Key.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static IntegrityReport Settled(AssemblyIntegrity check, string? waitingFor = null)
    {
        // El veredicto puede llegar en segundo plano: se espera a que deje de estar «en curso».
        var deadline = DateTime.UtcNow.AddSeconds(10);
        IntegrityReport report = check.Current;
        while (report.Detail.Contains("en curso") && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            report = check.Current;
        }
        return report;
    }

    [Fact]
    public void WithoutAManifest_NothingIsClaimed_AndNothingThrows()
    {
        using var install = new Install();
        var report = Settled(install.Check());
        Assert.Equal(IntegrityVerdict.Unverified, report.Verdict);
        Assert.Contains("no hay manifiesto", report.Detail);
        Assert.Empty(report.Files);
    }

    [Fact]
    public void AGenuineInstall_IsIntact()
    {
        using var install = new Install();
        install.SignManifest();
        var report = Settled(install.Check());
        Assert.Equal(IntegrityVerdict.Intact, report.Verdict);
        Assert.Equal(IntegrityManifest.SensitiveFiles.Count, report.Files.Count);
        Assert.All(report.Files, f => Assert.Equal(IntegrityFileStatus.Match, f.Status));
    }

    [Fact]
    public void APatchedAssembly_IsTampered_AndNamed()
    {
        using var install = new Install();
        install.SignManifest();
        install.Patch("Baioss.Record.Infrastructure.dll");

        var report = Settled(install.Check());

        Assert.Equal(IntegrityVerdict.Tampered, report.Verdict);
        Assert.Equal(new[] { "Baioss.Record.Infrastructure.dll" }, report.MismatchedFiles);
    }

    [Fact]
    public void AManifestFromAnotherKey_OrAStaleOne_NeverThrows_AndOnlyTheStaleOneIsPositive()
    {
        using var install = new Install();

        // Firmado por otro (un atacante con su propia clave, o la clave equivocada al publicar): no se afirma nada.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        install.SignManifest(with: other);
        var foreign = Settled(install.Check());
        Assert.Equal(IntegrityVerdict.Unverified, foreign.Verdict);
        Assert.Contains("firma", foreign.Detail);

        // Manifiesto auténtico pero DESFASADO (se reemplazaron DLL sin volver a firmar): eso SÍ es evidencia
        // positiva — es exactamente el caso «instalación mezclada» y la salida es volver a firmar o reinstalar.
        install.SignManifest();
        install.Patch("Baioss.Record.Application.dll");
        Assert.Equal(IntegrityVerdict.Tampered, Settled(install.Check()).Verdict);
    }

    [Fact]
    public void ALockedAssembly_IsUnverified_NotTampered_AndTheVerdictIsPerProcess()
    {
        using var install = new Install();
        install.SignManifest();
        string locked = Path.Combine(install.Dir, "Baioss.Record.App.dll");

        IntegrityReport whileLocked;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) // el antivirus lo tiene abierto en exclusiva
            whileLocked = Settled(install.Check());
        Assert.Equal(IntegrityVerdict.Unverified, whileLocked.Verdict);
        Assert.Equal(IntegrityFileStatus.Unreadable, whileLocked.Files.Single(f => f.File == "Baioss.Record.App.dll").Status);
        Assert.Contains("no se pudo leer", whileLocked.Detail);

        // Liberado el archivo, una comprobación NUEVA (otro arranque) lo ve intacto.
        Assert.Equal(IntegrityVerdict.Intact, Settled(install.Check()).Verdict);
    }

    [Fact]
    public void ALockedManifest_IsUnverified()
    {
        using var install = new Install();
        install.SignManifest();
        IntegrityReport report;
        using (new FileStream(install.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
            report = Settled(install.Check());
        Assert.Equal(IntegrityVerdict.Unverified, report.Verdict);
        Assert.Contains("no se pudo leer el manifiesto", report.Detail);
    }

    [Fact]
    public void AMissingFolder_OrAReaderThatThrows_IsUnverified_AndNeverThrows()
    {
        var nowhere = new AssemblyIntegrity(NullLogger.Instance, Path.Combine(Path.GetTempPath(), "baioss-no-existe-" + Guid.NewGuid().ToString("N")),
            "no-es-una-clave", null, TimeSpan.FromSeconds(5));
        Assert.Equal(IntegrityVerdict.Unverified, Settled(nowhere).Verdict);

        using var install = new Install();
        install.SignManifest();
        var exploding = Settled(install.Check(hashFile: _ => throw new IOException("disco que no responde")));
        Assert.Equal(IntegrityVerdict.Unverified, exploding.Verdict);
        Assert.All(exploding.Files, f => Assert.Equal(IntegrityFileStatus.Unreadable, f.Status));
    }

    [Fact]
    public void ASlowDisk_DoesNotHoldTheFirstQueryBeyondItsBudget_AndTheVerdictArrivesLater()
    {
        using var install = new Install();
        install.SignManifest();
        install.Patch("Baioss.Record.Engine.FFmpeg.dll");
        using var gate = new ManualResetEventSlim(false);
        var realHashes = install.Hashes();
        Func<string, string?> slow = name => { gate.Wait(); return realHashes.TryGetValue(name, out var h) ? h : null; };

        var check = install.Check(hashFile: slow, firstWait: TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        var first = check.Current;
        sw.Stop();

        Assert.Equal(IntegrityVerdict.Unverified, first.Verdict);   // «en curso»: no se afirma nada todavía
        Assert.Contains("en curso", first.Detail);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"la primera consulta tardó {sw.Elapsed}");
        Assert.Contains("en curso", check.Current.Detail);            // las siguientes no vuelven a esperar
        Assert.Contains("en curso", check.Current.Detail);

        gate.Set(); // el disco responde
        var settled = Settled(check);
        Assert.Equal(IntegrityVerdict.Tampered, settled.Verdict);     // y el veredicto real llega (lo recoge el siguiente ciclo)
        Assert.Equal(new[] { "Baioss.Record.Engine.FFmpeg.dll" }, settled.MismatchedFiles);
    }
}
