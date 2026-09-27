using System.Buffers.Binary;
using System.Text;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Reconocimiento de la disposición de un MP4 por sus cajas de primer nivel: fragmentado (moof), estándar con el
/// índice al inicio (finalizado) o al final.</summary>
public class Mp4LayoutTests
{
    private static byte[] Box(string type, int payload, bool largesize = false)
    {
        int headerLength = largesize ? 16 : 8;
        var box = new byte[headerLength + payload];
        if (largesize)
        {
            BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8, 8), (ulong)box.Length);
        }
        else BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        for (int i = headerLength; i < box.Length; i++) box[i] = (byte)(i * 7);
        return box;
    }

    private static Mp4Layout.Layout Inspect(params byte[][] boxes)
    {
        using var ms = new MemoryStream();
        foreach (var b in boxes) ms.Write(b);
        ms.Position = 0;
        return Mp4Layout.Inspect(ms);
    }

    [Fact]
    public void AFaststartFile_IsFinalized()
    {
        var layout = Inspect(Box("ftyp", 24), Box("moov", 500), Box("free", 8), Box("mdat", 4000));
        Assert.False(layout.Fragmented);
        Assert.True(layout.MoovBeforeMdat);
        Assert.True(layout.IsFinalized);
    }

    [Fact]
    public void AFragmentedFile_IsNotFinalized_EvenThoughItsEmptyMoovComesFirst()
    {
        var layout = Inspect(Box("ftyp", 24), Box("moov", 300), Box("moof", 120), Box("mdat", 4000), Box("moof", 120), Box("mdat", 4000), Box("mfra", 60));
        Assert.True(layout.Fragmented);
        Assert.True(layout.MoovBeforeMdat);
        Assert.False(layout.IsFinalized);
    }

    [Fact]
    public void AStandardFileWithTheIndexAtTheEnd_IsNotFinalized()
    {
        var layout = Inspect(Box("ftyp", 24), Box("free", 8), Box("mdat", 4000), Box("moov", 500));
        Assert.False(layout.Fragmented);
        Assert.True(layout.HasMoov);
        Assert.False(layout.MoovBeforeMdat);
        Assert.False(layout.IsFinalized);
    }

    [Fact]
    public void AGrowingStandardFile_HasNoMoovYet_AndLargeBoxesAreSkippedByTheirHeader()
    {
        var layout = Inspect(Box("ftyp", 24), Box("free", 8), Box("mdat", 4000, largesize: true));
        Assert.False(layout.HasMoov);
        Assert.False(layout.IsFinalized);
        // Una cabecera rota (tamaño < 8) detiene el recorrido sin lanzar.
        var broken = new byte[] { 0, 0, 0, 2, (byte)'x', (byte)'x', (byte)'x', (byte)'x' };
        Assert.False(Inspect(Box("ftyp", 24), broken).HasMoov);
    }
}
