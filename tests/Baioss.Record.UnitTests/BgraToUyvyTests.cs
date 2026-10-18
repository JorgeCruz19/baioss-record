using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// El BGRA de una fuente NDI con alfa, en UYVY: los colores de referencia de BT.709 (HD) y BT.601 (SD) en rango limitado, el croma
/// de cada par como la media de los dos píxeles y el alfa fuera.
/// </summary>
public sealed class BgraToUyvyTests
{
    private static byte[] Convert(int frameHeight, params (byte R, byte G, byte B, byte A)[] pixels)
    {
        var bgra = pixels.SelectMany(p => new[] { p.B, p.G, p.R, p.A }).ToArray();
        var uyvy = new byte[pixels.Length * 2];
        BgraToUyvy.ConvertLine(bgra, uyvy, pixels.Length, frameHeight);
        return uyvy;
    }

    [Theory]
    [InlineData(255, 255, 255, 235, 128, 128)] // blanco
    [InlineData(0, 0, 0, 16, 128, 128)]        // negro
    [InlineData(128, 128, 128, 126, 128, 128)] // gris: U y V en 128 exacto
    [InlineData(255, 0, 0, 63, 102, 240)]      // rojo
    [InlineData(0, 255, 0, 173, 42, 26)]       // verde
    [InlineData(0, 0, 255, 32, 240, 118)]      // azul
    public void HdColors_UseBt709_LimitedRange(byte r, byte g, byte b, byte y, byte u, byte v)
        => Assert.Equal(new[] { u, y, v, y }, Convert(1080, (r, g, b, 255), (r, g, b, 255)));

    [Theory]
    [InlineData(576, 255, 0, 0, 81, 90, 240)]
    [InlineData(486, 0, 255, 0, 145, 54, 34)]
    [InlineData(480, 0, 0, 255, 41, 240, 110)]
    public void SdColors_UseBt601(int height, byte r, byte g, byte b, byte y, byte u, byte v)
        => Assert.Equal(new[] { u, y, v, y }, Convert(height, (r, g, b, 255), (r, g, b, 255)));

    [Fact]
    public void EachPairSharesTheAverageChroma_AndKeepsItsOwnLuma()
        // Rojo y azul juntos: el croma del morado que hacen entre los dos; la luma, la de cada uno.
        => Assert.Equal(new byte[] { 171, 63, 179, 32 }, Convert(1080, (255, 0, 0, 255), (0, 0, 255, 255)));

    [Fact]
    public void AlphaIsDropped()
        => Assert.Equal(Convert(1080, (200, 100, 50, 255), (10, 20, 30, 255)), Convert(1080, (200, 100, 50, 0), (10, 20, 30, 96)));

    [Fact]
    public void AWholeLine_IsConvertedPairByPair()
    {
        var pixels = Enumerable.Range(0, 1920).Select(x => ((byte)(x % 256), (byte)(x / 8 % 256), (byte)(255 - x % 256), (byte)255)).ToArray();
        var line = Convert(1080, pixels);
        for (int x = 0; x < pixels.Length; x += 2)
            Assert.Equal(Convert(1080, pixels[x], pixels[x + 1]), line[(2 * x)..(2 * x + 4)]);
    }

    [Theory]
    [InlineData(1920, true)]
    [InlineData(720, true)]
    [InlineData(1919, false)] // UYVY lleva un U/V por cada par de píxeles
    [InlineData(0, false)]
    public void NeedsAnEvenWidth(int width, bool supported)
    {
        Assert.Equal(supported, BgraToUyvy.Supports(width));
        if (!supported && width > 0)
            Assert.Throws<ArgumentOutOfRangeException>(() => BgraToUyvy.ConvertLine(new byte[width * 4], new byte[width * 2 + 2], width, 1080));
    }
}
