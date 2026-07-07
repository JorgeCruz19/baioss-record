using Baioss.Record.Infrastructure.Storage;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Validación de seguridad de la retención (Fase 1): NUNCA borrar un archivo EN USO (grabándose, exportándose,
/// reproduciéndose o bloqueado por otro proceso). <see cref="StorageManager.IsFileInUse"/> lo detecta intentando
/// abrirlo en exclusiva. (La exclusión de grabaciones PROTEGIDAS se verifica en vivo: depende de BD + archivos.)
/// </summary>
public sealed class StorageRetentionSafetyTests
{
    [Fact]
    public void IsFileInUse_TrueWhenOpenExclusively_FalseWhenFree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"baioss-inuse-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "x");
        try
        {
            Assert.False(StorageManager.IsFileInUse(path)); // libre → se puede borrar
            using (var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.True(StorageManager.IsFileInUse(path)); // abierto en exclusiva → EN USO, no borrar
            Assert.False(StorageManager.IsFileInUse(path)); // cerrado → libre otra vez
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    [Fact]
    public void IsFileInUse_TrueWhenAnotherHandleHoldsIt()
    {
        // Un archivo abierto por otro proceso (aunque comparta lectura, típico de un reproductor/exportador) NO
        // concede la apertura ReadWrite exclusiva → cuenta como EN USO y no se borra mientras esté abierto.
        var path = Path.Combine(Path.GetTempPath(), $"baioss-inuse2-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "x");
        try
        {
            using (var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.True(StorageManager.IsFileInUse(path));
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    [Fact]
    public void IsFileInUse_FalseWhenFileMissing()
    {
        // Un archivo inexistente no está «en uso» (el borrado simplemente lo ignora); no debe reportar en uso.
        Assert.False(StorageManager.IsFileInUse(Path.Combine(Path.GetTempPath(), $"baioss-none-{Guid.NewGuid():N}.tmp")));
    }
}

/// <summary>
/// Fase 2 — objetivo de espacio libre de la retención por ESPACIO: el MAYOR entre N GB y N% del total (0 = off).
/// </summary>
public sealed class StorageFreeTargetTests
{
    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void Disabled_WhenBothZero() => Assert.Equal(0, StorageManager.ComputeFreeTarget(1000 * GB, 0, 0));

    [Fact]
    public void GBOnly() => Assert.Equal(100 * GB, StorageManager.ComputeFreeTarget(1000 * GB, 100, 0));

    [Fact]
    public void PercentOnly_TenPercentOfThousand() // 10% de 1000 GB = 100 GB
        => Assert.Equal(100 * GB, StorageManager.ComputeFreeTarget(1000 * GB, 0, 10));

    [Fact]
    public void CombinedTakesLarger() // 50 GB vs 10% (=100 GB) → gana 100 GB
        => Assert.Equal(100 * GB, StorageManager.ComputeFreeTarget(1000 * GB, 50, 10));

    [Fact]
    public void PercentIgnored_WhenTotalUnknown() // sin total, solo cuenta el objetivo en GB
        => Assert.Equal(50 * GB, StorageManager.ComputeFreeTarget(0, 50, 10));
}
