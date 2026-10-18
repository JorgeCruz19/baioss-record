using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// El vídeo NDI en NUT para un proceso de FFmpeg (<see cref="NdiNutFraming"/>): sus cabeceras con el formato de su primer cuadro y
/// las marcas desde ese cuadro, que es el 0; lo que no cabe en su flujo (otro formato, un periodo que no avanza) no se le escribe.
/// </summary>
public sealed class NdiNutFramingTests
{
    private static readonly NdiVideoFormat Uyvy1080 = new(1920, 1080, "uyvy422", 30000, 1001);
    private static NdiVideoItem Item(long period, NdiVideoFormat? format = null)
        => new(new NdiFrame(new byte[1], 1920 * 1080 * 2, format ?? Uyvy1080), period);

    /// <summary>La marca (en periodos) de una cabecera de cuadro escrita por el enmarcado.</summary>
    private static long PeriodOf(ReadOnlyMemory<byte> header)
    {
        var bytes = header.ToArray();
        int pos = 1;
        Assert.True(NutStreamReader.TryReadVarint(bytes, ref pos, out _));   // banderas
        Assert.True(NutStreamReader.TryReadVarint(bytes, ref pos, out var pts));
        return ((long)pts - 128) / NutRawVideoWriter.TicksPerPeriod;
    }

    [Fact]
    public void TheFirstFrame_CarriesTheHeaders_AndTheMarksCountFromIt()
    {
        var framing = new NdiNutFraming();
        Assert.True(framing.TryPrefix(Item(1000), out var first));
        var preamble = new NutRawVideoWriter(1920, 1080, "uyvy422", 30000, 1001).Preamble;
        Assert.True(first.Span[..preamble.Length].SequenceEqual(preamble));
        Assert.Equal(0, PeriodOf(first[preamble.Length..]));

        Assert.True(framing.TryPrefix(Item(1003), out var later));
        Assert.Equal(3, PeriodOf(later));
        Assert.True(framing.TryPrefix(Item(1004), out later));
        Assert.Equal(4, PeriodOf(later));
        Assert.Equal(0, framing.Skipped);
    }

    [Fact]
    public void AFrameThatDoesNotAdvance_OrOfAnotherFormat_IsNotWritten()
    {
        var framing = new NdiNutFraming();
        Assert.True(framing.TryPrefix(Item(10), out _));
        Assert.False(framing.TryPrefix(Item(10), out _));                                         // mismo periodo
        Assert.False(framing.TryPrefix(Item(9), out _));                                          // hacia atrás
        Assert.False(framing.TryPrefix(Item(11, Uyvy1080 with { Width = 1280, Height = 720 }), out _)); // la fuente cambió en caliente
        Assert.False(framing.TryPrefix(new NdiVideoItem(new NdiFrame(new byte[1], 1), 12), out _));     // sin formato
        Assert.True(framing.TryPrefix(Item(13), out var next));                                     // el formato de sus cabeceras, sí
        Assert.Equal(3, PeriodOf(next));
        Assert.Equal(4, framing.Skipped);
    }
}
