using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Detección PURA de cambio de formato NDI EN CALIENTE (N20): resolución o pixel distintos ⇒ cambio.</summary>
public sealed class NdiReceiverFormatTests
{
    [Fact]
    public void SameFormat_IsNotAChange()
        => Assert.False(NdiReceiver.FormatDiffers(1920, 1080, "uyvy422", 1920, 1080, "uyvy422"));

    [Theory]
    [InlineData(1280, 720, "uyvy422")]   // resolución distinta (720 vs 1080)
    [InlineData(1920, 1080, "bgra")]     // mismo tamaño, pixel distinto (uyvy422 → bgra)
    [InlineData(1280, 1080, "uyvy422")]  // solo cambia el ancho
    public void DifferentResolutionOrPixel_IsAChange(int w, int h, string pix)
        => Assert.True(NdiReceiver.FormatDiffers(w, h, pix, oldW: 1920, oldH: 1080, oldPix: "uyvy422"));
}
