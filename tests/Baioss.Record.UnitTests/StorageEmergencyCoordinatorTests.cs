using Baioss.Record.Application.Storage;
using Baioss.Record.Infrastructure.Storage;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Fase 3b — lógica PURA del coordinador global de emergencia de almacenamiento:
/// <list type="bullet">
/// <item><see cref="StorageEmergencyCoordinator.IsEmergency"/>: transición con HISTÉRESIS (entra al alcanzar el
/// umbral; una vez dentro, solo sale al bajar del umbral menos la histéresis → no oscila en el borde).</item>
/// <item><see cref="StorageEmergencyCoordinator.CleanupTargetFreePercent"/>: objetivo de % libre de la auto-limpieza
/// de emergencia (dejar el disco por debajo del umbral crítico).</item>
/// </list>
/// El comportamiento COMPLETO (vigilancia continua, auto-limpieza opt-in, bloqueo de nuevas grabaciones) se
/// verifica en vivo: depende de la medida real del volumen + BD + bus.
/// </summary>
public sealed class StorageEmergencyCoordinatorTests
{
    // --- IsEmergency: histéresis de entrada/salida (umbral 95, histéresis 2 → sale al bajar de 93) ---

    [Fact]
    public void Enters_AtOrAboveThreshold_WhenNotYetInEmergency()
    {
        Assert.True(StorageEmergencyCoordinator.IsEmergency(95.0, wasEmergency: false, emergencyPercent: 95, hysteresisPoints: 2));
        Assert.True(StorageEmergencyCoordinator.IsEmergency(97.3, wasEmergency: false, emergencyPercent: 95, hysteresisPoints: 2));
    }

    [Fact]
    public void DoesNotEnter_BelowThreshold_WhenNotYetInEmergency()
    {
        Assert.False(StorageEmergencyCoordinator.IsEmergency(94.9, wasEmergency: false, emergencyPercent: 95, hysteresisPoints: 2));
        Assert.False(StorageEmergencyCoordinator.IsEmergency(80.0, wasEmergency: false, emergencyPercent: 95, hysteresisPoints: 2));
    }

    [Fact]
    public void StaysInEmergency_WithinHysteresisBand()
    {
        // Ya en emergencia y ocupado en 94% (entre 93 y 95): NO sale todavía (evita oscilación en el borde).
        Assert.True(StorageEmergencyCoordinator.IsEmergency(94.0, wasEmergency: true, emergencyPercent: 95, hysteresisPoints: 2));
        Assert.True(StorageEmergencyCoordinator.IsEmergency(93.0, wasEmergency: true, emergencyPercent: 95, hysteresisPoints: 2)); // 93 ≥ 93
    }

    [Fact]
    public void LeavesEmergency_OnceBelowHysteresisBand()
    {
        // Ya en emergencia pero ocupado bajó a 92.9% (< 93): SALE de la emergencia.
        Assert.False(StorageEmergencyCoordinator.IsEmergency(92.9, wasEmergency: true, emergencyPercent: 95, hysteresisPoints: 2));
    }

    [Fact]
    public void Disabled_WhenThresholdZero_NeverEmergency()
    {
        Assert.False(StorageEmergencyCoordinator.IsEmergency(99.9, wasEmergency: false, emergencyPercent: 0, hysteresisPoints: 2));
        Assert.False(StorageEmergencyCoordinator.IsEmergency(99.9, wasEmergency: true, emergencyPercent: 0, hysteresisPoints: 2));
    }

    // --- CleanupTargetFreePercent: dejar el disco por debajo del umbral crítico (100 − crítico) ---

    [Fact]
    public void CleanupTarget_IsComplementOfCritical()
    {
        Assert.Equal(10, StorageEmergencyCoordinator.CleanupTargetFreePercent(90)); // crítico 90% → dejar ≥ 10% libre
        Assert.Equal(5, StorageEmergencyCoordinator.CleanupTargetFreePercent(95));  // crítico 95% → dejar ≥ 5% libre
    }

    [Fact]
    public void CleanupTarget_DefaultsToTenPercent_WhenCriticalOutOfRange()
    {
        Assert.Equal(10, StorageEmergencyCoordinator.CleanupTargetFreePercent(0));
        Assert.Equal(10, StorageEmergencyCoordinator.CleanupTargetFreePercent(100));
    }

    // --- HealthFor: nivel del indicador de la UI por % ocupado (umbrales 80/90/95) (Fase 4b) ---

    [Theory]
    [InlineData(50, StorageHealth.Ok)]
    [InlineData(79.9, StorageHealth.Ok)]
    [InlineData(80, StorageHealth.Warning)]
    [InlineData(85, StorageHealth.Warning)]
    [InlineData(90, StorageHealth.Critical)]
    [InlineData(92, StorageHealth.Critical)]
    [InlineData(95, StorageHealth.Emergency)]
    [InlineData(99.5, StorageHealth.Emergency)]
    public void HealthFor_MapsUsedPercentToLevel(double usedPct, StorageHealth expected)
        => Assert.Equal(expected, StorageEmergencyCoordinator.HealthFor(usedPct, 80, 90, 95));

    [Fact]
    public void HealthFor_ZeroThresholds_AreDisabled()
        => Assert.Equal(StorageHealth.Ok, StorageEmergencyCoordinator.HealthFor(99.9, 0, 0, 0));

    // --- NormalizeVolume: clasifica una carpeta de destino por DISCO (raíz del volumen) (multi-disco) ---

    [Theory]
    [InlineData(@"D:\capturer01\A_20260721.mp4", @"D:\")]
    [InlineData(@"D:\capturer01", @"D:\")]
    [InlineData(@"C:\Users\x\recordings", @"C:\")]
    public void NormalizeVolume_ReturnsDriveRoot(string path, string expected)
        => Assert.Equal(expected, StorageEmergencyCoordinator.NormalizeVolume(path));

    [Fact]
    public void NormalizeVolume_SameDrive_DifferentFolders_ShareVolume()
        => Assert.Equal(StorageEmergencyCoordinator.NormalizeVolume(@"D:\a"),
                        StorageEmergencyCoordinator.NormalizeVolume(@"D:\b\c\d"));

    [Fact]
    public void NormalizeVolume_DifferentDrives_DifferValues()
        => Assert.NotEqual(StorageEmergencyCoordinator.NormalizeVolume(@"C:\rec"),
                           StorageEmergencyCoordinator.NormalizeVolume(@"D:\rec"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeVolume_BlankPath_IsNull(string path)
        => Assert.Null(StorageEmergencyCoordinator.NormalizeVolume(path));
}
