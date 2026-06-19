using Baioss.Record.Domain;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Enumeración REAL de dispositivos contra el FFmpeg incluido: valida de punta a punta que lanzar
/// el proceso y parsear su salida funciona. No exige hardware: en una máquina sin cámara/tarjeta las
/// listas vienen vacías, pero la llamada no debe lanzar excepción.
/// </summary>
public sealed class CaptureDeviceEnumerationTests
{
    [SkippableFact]
    public async Task DiscoverDshow_RunsAndReturnsConsistentDevices()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg no disponible en tools/.");
        var enumerator = new FfmpegDeviceEnumerator(new FfmpegLocator(TestAssets.FfmpegDir!));

        var video = await enumerator.DiscoverAsync(InputType.DirectShow);
        var audio = await enumerator.DiscoverAudioDevicesAsync(InputType.DirectShow);

        Assert.NotNull(video);
        Assert.NotNull(audio);
        Assert.All(video, d => Assert.Equal(InputType.DirectShow, d.Type));
        Assert.All(video, d => Assert.False(string.IsNullOrWhiteSpace(d.Uri)));
    }

    [SkippableFact]
    public async Task DiscoverDecklink_DoesNotThrowWhenNoCardPresent()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg no disponible en tools/.");
        var enumerator = new FfmpegDeviceEnumerator(new FfmpegLocator(TestAssets.FfmpegDir!));

        var devices = await enumerator.DiscoverAsync(InputType.DecklinkSdi);

        Assert.NotNull(devices); // sin tarjeta/driver → lista vacía, pero sin excepción
        Assert.All(devices, d => Assert.Equal(InputType.DecklinkSdi, d.Type));
    }
}
