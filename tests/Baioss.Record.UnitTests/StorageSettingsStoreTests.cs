using Baioss.Record.Application.Storage;
using Baioss.Record.Domain;
using Baioss.Record.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Fase 4c — saneado de los ajustes de almacenamiento (rangos + orden coherente de umbrales).</summary>
public sealed class StorageSettingsSanitizeTests
{
    [Fact]
    public void ClampsPercentsToRange()
    {
        var s = new StorageSettings { WarnPercent = 150, CriticalPercent = -3, EmergencyPercent = 999 }.Sanitized();
        Assert.InRange(s.WarnPercent, 0, 100);
        Assert.InRange(s.CriticalPercent, 0, 100);
        Assert.InRange(s.EmergencyPercent, 0, 100);
    }

    [Fact]
    public void OrdersWarnLeCriticalLeEmergency()
    {
        var s = new StorageSettings { WarnPercent = 95, CriticalPercent = 60, EmergencyPercent = 80 }.Sanitized();
        Assert.True(s.WarnPercent <= s.CriticalPercent);
        Assert.True(s.CriticalPercent <= s.EmergencyPercent);
    }

    [Theory]
    [InlineData(2, 5)]   // positivo < 5 → 5 (mínimo)
    [InlineData(0, 0)]   // 0 = usar el defecto de horas (se conserva 0)
    [InlineData(30, 30)] // válido, sin cambios
    public void IntervalMinimumFiveWhenPositive(int input, int expected)
        => Assert.Equal(expected, new StorageSettings { IntervalMinutes = input }.Sanitized().IntervalMinutes);

    [Fact]
    public void ArchiveWithoutPathFallsBackToDelete()
    {
        var s = new StorageSettings { Action = RetentionAction.Archive, ArchivePath = "  " }.Sanitized();
        Assert.Equal(RetentionAction.Delete, s.Action);
    }
}

/// <summary>Fase 4c — almacén JSON de ajustes: siembra, persistencia entre instancias, copia y saneado en Save.</summary>
public sealed class JsonStorageSettingsStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"baioss-settings-{Guid.NewGuid():N}.json");
    private static JsonStorageSettingsStore Store(string path, StorageSettings seed)
        => new(path, seed, NullLogger<JsonStorageSettingsStore>.Instance);

    [Fact]
    public void SeedsFileWhenMissing_AndReturnsSeed()
    {
        var path = TempPath();
        try
        {
            var store = Store(path, new StorageSettings { RetentionEnabled = true, RetentionDays = 15, EmergencyPercent = 88 });
            Assert.True(File.Exists(path)); // creó el archivo al sembrar
            Assert.True(store.Current.RetentionEnabled);
            Assert.Equal(15, store.Current.RetentionDays);
            Assert.Equal(88, store.Current.EmergencyPercent);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    [Fact]
    public void SavePersists_AndReloadsAcrossInstances()
    {
        var path = TempPath();
        try
        {
            var store1 = Store(path, new StorageSettings());
            var edited = store1.Current;
            edited.RetentionEnabled = true;
            edited.MinFreePercent = 20;
            edited.AutoCleanupOnEmergency = true;
            store1.Save(edited);

            var store2 = Store(path, new StorageSettings()); // instancia NUEVA sobre el mismo archivo
            Assert.True(store2.Current.RetentionEnabled);
            Assert.Equal(20, store2.Current.MinFreePercent);
            Assert.True(store2.Current.AutoCleanupOnEmergency);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    [Fact]
    public void CurrentReturnsCopy_NotSharedReference()
    {
        var path = TempPath();
        try
        {
            var store = Store(path, new StorageSettings { RetentionDays = 30 });
            var copy = store.Current;
            copy.RetentionDays = 999; // mutar la copia NO afecta al almacén
            Assert.Equal(30, store.Current.RetentionDays);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    [Fact]
    public void SaveSanitizesOutOfRangeValues()
    {
        var path = TempPath();
        try
        {
            var store = Store(path, new StorageSettings());
            var bad = store.Current;
            bad.EmergencyPercent = 300;
            bad.IntervalMinutes = 1;
            store.Save(bad);
            Assert.InRange(store.Current.EmergencyPercent, 0, 100);
            Assert.Equal(5, store.Current.IntervalMinutes);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }
}
