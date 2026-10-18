using System.Text;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// El NUT que el receptor NDI escribe para FFmpeg: solo los cuadros que llegan, cada uno con su periodo. Lo que FFmpeg lee de
/// verdad lo prueba la suite de integración (ffprobe y una grabación); aquí, la forma del flujo: la suma igual que la de FFmpeg
/// (si no, rechaza las cabeceras), el flujo entero se trocea con el lector de NUT del relé DeckLink, y cada cuadro lleva su
/// marca completa y su tamaño.
/// </summary>
public sealed class NutRawVideoWriterTests
{
    [Fact]
    public void TheChecksum_IsFfmpegs()
    {
        // La cabecera principal de un NUT escrito por FFmpeg (rawvideo uyvy422) y la suma que guardó tras ella.
        var payload = Convert.FromHexString(
            "030181ff7f010183e000c00006000000000001a000020101280100290021018f7f20028f7f8179c00006000000000001060300000104000001b602fffa02fffb02fffc02fffd");
        Assert.Equal(0xC62EA075u, NutRawVideoWriter.Crc(payload));
        Assert.Equal(0x89A1897Fu, NutRawVideoWriter.Crc("123456789"u8)); // CRC-32/CKSUM sin el xor final
    }

    [Theory]
    [InlineData(0UL, "00")]
    [InlineData(127UL, "7f")]
    [InlineData(128UL, "8100")]
    [InlineData(32767UL, "81ff7f")]
    [InlineData(61440UL, "83e000")]
    public void Varints_AreNuts(ulong value, string hex)
    {
        var buffer = new byte[10];
        int n = NutRawVideoWriter.PutVarint(buffer, value);
        Assert.Equal(hex, Convert.ToHexString(buffer, 0, n).ToLowerInvariant());
    }

    [Fact]
    public async Task TheStream_SplitsIntoItsUnits_WithTheDeclaredRateAndAFrameHeaderPerFrame()
    {
        var writer = new NutRawVideoWriter(16, 8, "uyvy422", 30000, 1001);
        var stream = new MemoryStream();
        stream.Write(writer.Preamble);
        var header = new byte[NutRawVideoWriter.MaxFrameHeaderBytes];
        long[] periods = { 0, 3, 4, 9, 300_000 };
        foreach (var period in periods)
        {
            int n = writer.WriteFrameHeader(header, period, 16 * 8 * 2);
            stream.Write(header, 0, n);
            stream.Write(new byte[16 * 8 * 2]);
        }
        stream.Position = 0;

        var reader = new NutStreamReader(stream, pooled: false);
        var kinds = new List<NutUnitKind>();
        var frames = new List<NutUnit>();
        while (await reader.ReadUnitAsync() is { } unit)
        {
            kinds.Add(unit.Kind);
            if (unit.Kind == NutUnitKind.Frame) frames.Add(unit);
        }
        Assert.Equal(new[] { NutUnitKind.FileId, NutUnitKind.MainHeader, NutUnitKind.StreamHeader, NutUnitKind.Info, NutUnitKind.Syncpoint },
                     kinds.Take(5));
        Assert.Equal(4, reader.Version);    // modo tubería: un solo punto de sincronía, delante del primer cuadro
        Assert.Equal(1, reader.StreamCount);
        Assert.Equal(periods.Length, frames.Count);
        Assert.All(frames, f => Assert.True(f.IsVideoFrame));

        for (int i = 0; i < frames.Count; i++) AssertFrame(frames[i].Bytes.ToArray(), periods[i], 256);

        // La información del flujo lleva la cadencia declarada: con ella FFmpeg da a cada cuadro la duración de un periodo.
        Assert.Contains("r_frame_rate", Encoding.ASCII.GetString(writer.Preamble));
        Assert.Contains("30000/1001", Encoding.ASCII.GetString(writer.Preamble));
        Assert.Contains("UYVY", Encoding.ASCII.GetString(writer.Preamble));
    }

    [Fact]
    public void TheTimeBase_IsHalfAPeriod_Reduced()
    {
        // FFmpeg rechaza una base de tiempo no irreducible («invalid time base 1000/120000»): 60000/1000 → 1/120.
        var preamble = new NutRawVideoWriter(16, 8, "bgra", 60000, 1000).Preamble;
        var main = MainHeaderPayload(preamble).ToArray();
        int pos = 0;
        ulong Next() { Assert.True(NutStreamReader.TryReadVarint(main, ref pos, out var v)); return v; }
        Assert.Equal(4UL, Next());      // versión
        Assert.Equal(1UL, Next());      // versión menor
        Assert.Equal(1UL, Next());      // flujos
        Next();                         // max_distance
        Assert.Equal(1UL, Next());      // bases de tiempo
        Assert.Equal(1UL, Next());
        Assert.Equal(120UL, Next());
        Assert.Contains("BGRA", Encoding.ASCII.GetString(preamble));
    }

    [Fact]
    public void AnUnknownPixelFormat_OrANegativeMark_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new NutRawVideoWriter(16, 8, "yuv420p", 25, 1));
        var writer = new NutRawVideoWriter(16, 8, "uyvy422", 25, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteFrameHeader(new byte[NutRawVideoWriter.MaxFrameHeaderBytes], -1, 256));
    }

    /// <summary>Un cuadro: código 0, banderas codificadas (clave, marca, tamaño, suma), la marca completa (2·periodo + 1&lt;&lt;7), el
    /// tamaño, la suma de la cabecera y los datos.</summary>
    private static void AssertFrame(byte[] bytes, long period, int dataBytes)
    {
        int pos = 0;
        Assert.Equal(0, bytes[pos++]);
        Assert.True(NutStreamReader.TryReadVarint(bytes, ref pos, out var flags));
        Assert.Equal(1UL | 8 | 32 | 64, flags);
        Assert.True(NutStreamReader.TryReadVarint(bytes, ref pos, out var pts));
        Assert.Equal((ulong)(period * NutRawVideoWriter.TicksPerPeriod + 128), pts);
        Assert.True(NutStreamReader.TryReadVarint(bytes, ref pos, out var size));
        Assert.Equal((ulong)dataBytes, size);
        Assert.Equal(NutRawVideoWriter.Crc(bytes.AsSpan(0, pos)), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos)));
        Assert.Equal(pos + 4 + dataBytes, bytes.Length);
    }

    private static ReadOnlySpan<byte> MainHeaderPayload(byte[] preamble)
    {
        int at = 25 + 8; // tras la cadena de identificación y el código de inicio
        int pos = at;
        Assert.True(NutStreamReader.TryReadVarint(preamble, ref pos, out var forward));
        return preamble.AsSpan(pos, (int)forward - 4);
    }
}
