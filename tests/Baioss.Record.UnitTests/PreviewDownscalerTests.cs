using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Reducción del frame de preview para las instantáneas del panel web.</summary>
public class PreviewDownscalerTests
{
    [Theory]
    [InlineData(640, 360, 320, 320, 180)]     // lo normal: mitad
    [InlineData(640, 360, 160, 160, 90)]
    [InlineData(640, 360, 1280, 640, 360)]    // nunca se amplía
    [InlineData(720, 576, 320, 320, 256)]     // SD 5:4 → alto proporcional y par
    [InlineData(640, 360, 321, 320, 180)]     // ancho impar → par
    [InlineData(640, 360, 0, 2, 2)]           // petición absurda → mínimo válido
    public void TargetSize_KeepsAspectNeverUpscalesAndStaysEven(int sw, int sh, int requested, int w, int h)
        => Assert.Equal((w, h), PreviewDownscaler.TargetSize(sw, sh, requested));

    [Fact]
    public void Downscale_AveragesEachBlock_AndWritesOpaqueAlpha()
    {
        // 4×2 → 2×1: cada píxel destino es la media de un bloque 2×2 del origen.
        // Bloque izquierdo: B = 0,100,200,100 → 100. Bloque derecho: todo 255.
        byte[] src =
        {
            0, 10, 20, 0,    100, 10, 20, 0,    255, 255, 255, 7,   255, 255, 255, 7,
            200, 10, 20, 0,  100, 10, 20, 0,    255, 255, 255, 7,   255, 255, 255, 7,
        };
        var dst = new byte[2 * 1 * 4];

        PreviewDownscaler.Downscale(src, 4, 2, 16, dst, 2, 1);

        Assert.Equal(new byte[] { 100, 10, 20, 255, 255, 255, 255, 255 }, dst);
    }

    [Fact]
    public void Downscale_HonoursTheSourceStride()
    {
        // Stride mayor que ancho×4 (relleno al final de cada fila): el relleno (99) no debe entrar en la media.
        byte[] src =
        {
            10, 10, 10, 0,  30, 30, 30, 0,  99, 99, 99, 99,
            50, 50, 50, 0,  70, 70, 70, 0,  99, 99, 99, 99,
        };
        var dst = new byte[4];

        PreviewDownscaler.Downscale(src, 2, 2, 12, dst, 1, 1);

        Assert.Equal(new byte[] { 40, 40, 40, 255 }, dst);
    }

    [Fact]
    public void Downscale_SameSize_IsACopyWithOpaqueAlpha()
    {
        byte[] src = { 1, 2, 3, 0, 4, 5, 6, 0 };
        var dst = new byte[8];

        PreviewDownscaler.Downscale(src, 2, 1, 8, dst, 2, 1);

        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, dst);
    }

    [Fact]
    public void Downscale_RejectsBuffersThatAreTooShort()
    {
        Assert.Throws<ArgumentException>(() => PreviewDownscaler.Downscale(new byte[8], 4, 2, 16, new byte[8], 2, 1));
        Assert.Throws<ArgumentException>(() => PreviewDownscaler.Downscale(new byte[32], 4, 2, 16, new byte[4], 2, 1));
    }
}
