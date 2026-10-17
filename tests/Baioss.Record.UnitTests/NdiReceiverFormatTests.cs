using Baioss.Record.Domain;
using Baioss.Record.Application.Capture;
using Baioss.Record.Infrastructure.Capture;
using NewTek;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Formato de una fuente NDI: cambio de formato EN CALIENTE (N20) y la señal que se publica (barrido incluido).</summary>
public sealed class NdiReceiverFormatTests
{
    [Fact]
    public void SameFormat_IsNotAChange()
        => Assert.False(NdiReceiver.FormatDiffers(1920, 1080, "uyvy422", false, 1920, 1080, "uyvy422", false));

    [Theory]
    [InlineData(1280, 720, "uyvy422", false)]   // resolución distinta (720 vs 1080)
    [InlineData(1920, 1080, "bgra", false)]     // mismo tamaño, pixel distinto (uyvy422 → bgra)
    [InlineData(1280, 1080, "uyvy422", false)]  // solo cambia el ancho
    [InlineData(1920, 1080, "uyvy422", true)]   // solo cambia el barrido (1080p29.97 → 1080i59.94)
    public void DifferentResolutionPixelOrScan_IsAChange(int w, int h, string pix, bool interlaced)
        => Assert.True(NdiReceiver.FormatDiffers(w, h, pix, interlaced, oldW: 1920, oldH: 1080, oldPix: "uyvy422", oldInterlaced: false));

    [Theory]
    [InlineData(NDIlib.frame_format_type_e.frame_format_type_interleaved, true)]
    [InlineData(NDIlib.frame_format_type_e.frame_format_type_progressive, false)]
    public void OnlyInterleavedFramesAreInterlaced(NDIlib.frame_format_type_e type, bool interlaced)
        => Assert.Equal(interlaced, NdiReceiver.IsInterleaved(type));

    [Fact]
    public void AnInterleavedSource_IsPublishedAsInterlacedTopFieldFirst_SoThePreviewAndRecordingDeinterlaceIt()
    {
        // Un 1080i de vMix por NDI: cuadros entretejidos a 30000/1001 (la tasa de cuadro). Antes la señal no decía nada del
        // barrido: preview con doble imagen en lo que se mueve y presets progresivos grabados sin desentrelazar.
        var signal = NdiCaptureSource.SignalFor(1920, 1080, 30000, 1001, interlaced: true, channels: 2, AudioSelection.FromParameters(null));
        Assert.Equal(ScanType.InterlacedTff, signal.Scan);
        Assert.Equal("1920×1080 · 59.94i · NDI", signal.FormatLabel);
        Assert.Equal(30000, signal.FrameRate!.Value.Numerator);
        Assert.True(signal.HasAudio); // aunque la fuente no traiga audio: el receptor sirve silencio
    }

    [Fact]
    public void AProgressiveSource_IsPublishedAsProgressive()
    {
        var signal = NdiCaptureSource.SignalFor(1920, 1080, 30000, 1001, interlaced: false, channels: 2, AudioSelection.FromParameters(null));
        Assert.Equal(ScanType.Progressive, signal.Scan);
        Assert.Equal("1920×1080 · 29.97p · NDI", signal.FormatLabel);
    }
}
