using Baioss.Record.Domain;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Descubrimiento NDI por el SDK (NDILibDotNetCoreBase): debe DEGRADAR LIMPIO cuando el runtime NDI no está
/// instalado (lista vacía, sin excepción) y, cuando está, devolver fuentes del tipo correcto.
/// </summary>
public sealed class NdiDiscoveryTests
{
    [SkippableFact]
    public async Task NdiDiscovery_DegradesCleanly_WhenRuntimeMissing()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg no disponible en tools/.");
        var enumerator = new FfmpegDeviceEnumerator(new FfmpegLocator(TestAssets.FfmpegDir!));

        var sources = await enumerator.DiscoverAsync(InputType.Ndi); // no debe lanzar en ningún caso

        if (!NdiRuntime.IsAvailable) Assert.Empty(sources); // sin runtime NDI → lista vacía
        Assert.All(sources, s => Assert.Equal(InputType.Ndi, s.Type));
    }
}
