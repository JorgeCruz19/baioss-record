using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// El lector NUT trocea un flujo real del muxer de FFmpeg (fixture generado con el FFmpeg empaquetado: 16×16 uyvy422 a
/// 5 fps + pcm_s16le mono, 1 s) en unidades enteras y en orden, sin perder ni inventar un byte; distingue vídeo de audio
/// por la clase del flujo; y ante un flujo truncado o desincronizado avisa en vez de entregar basura.
/// </summary>
public class NutStreamReaderTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.nut");

    private static async Task<List<NutUnit>> ReadAllAsync(Stream stream)
    {
        var reader = new NutStreamReader(stream, pooled: false);
        var units = new List<NutUnit>();
        while (await reader.ReadUnitAsync() is { } unit) units.Add(unit);
        return units;
    }

    [Fact]
    public async Task RealFixture_IsSplitIntoWholeUnits_InOrder()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        var units = await ReadAllAsync(new MemoryStream(bytes));

        Assert.Equal(bytes.Length, units.Sum(u => u.Length));
        Assert.Equal(NutUnitKind.FileId, units[0].Kind);
        Assert.Equal(NutUnitKind.MainHeader, units[1].Kind);
        Assert.Equal(new[] { NutUnitKind.StreamHeader, NutUnitKind.StreamHeader }, units.Skip(2).Take(2).Select(u => u.Kind));
        Assert.Equal(NutUnitKind.Index, units[^1].Kind);
        // Cabeceras (identificación, principal, 2 flujos, info) antes del primer punto de sincronía; después, nunca más.
        int firstSync = units.FindIndex(u => u.Kind == NutUnitKind.Syncpoint);
        Assert.True(units.Take(firstSync).All(u => u.IsHeader));
        Assert.DoesNotContain(units.Skip(firstSync), u => u.IsHeader);
        // Lo que ffprobe cuenta del fixture: 5 frames de vídeo de 512 bytes y 8 de audio (7 de 2048 + el último de 1664).
        var video = units.Where(u => u.IsVideoFrame).ToList();
        var audio = units.Where(u => u.IsAudioFrame).ToList();
        Assert.Equal(5, video.Count);
        Assert.Equal(8, audio.Count);
        Assert.All(video, u => Assert.True(u.Length > 512 && u.Length < 512 + 16, $"frame de vídeo de {u.Length} bytes"));
        Assert.Equal(7, audio.Count(u => u.Length is > 2048 and < 2048 + 16));
        Assert.All(units.Where(u => u.Kind == NutUnitKind.Frame), u => Assert.True(u.IsVideoFrame ^ u.IsAudioFrame));
        // Los bytes concatenados de las unidades son el archivo original, en orden.
        var joined = new MemoryStream();
        foreach (var u in units) joined.Write(u.Bytes.Span);
        Assert.Equal(bytes, joined.ToArray());
    }

    [Fact]
    public async Task RealFixture_DeclaresVersion3AndTwoStreams()
    {
        await using var stream = File.OpenRead(Fixture);
        var reader = new NutStreamReader(stream, pooled: false);
        await reader.ReadUnitAsync(); // FileId
        await reader.ReadUnitAsync(); // MainHeader
        Assert.Equal(3, reader.Version);
        Assert.Equal(2, reader.StreamCount);
    }

    [Fact]
    public async Task ChunkedDelivery_YieldsTheSameUnits()
    {
        // El socket entrega los bytes como quiere (a trozos de 7 bytes aquí): las unidades salen iguales.
        var bytes = await File.ReadAllBytesAsync(Fixture);
        var whole = await ReadAllAsync(new MemoryStream(bytes));
        var chunked = await ReadAllAsync(new TrickleStream(bytes, 7));
        Assert.Equal(whole.Select(u => (u.Kind, u.Length, u.StreamId)), chunked.Select(u => (u.Kind, u.Length, u.StreamId)));
    }

    [Fact]
    public async Task TruncatedInsideAFrame_Throws()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        var units = await ReadAllAsync(new MemoryStream(bytes));
        int firstVideo = units.FindIndex(u => u.IsVideoFrame);
        int cut = units.Take(firstVideo).Sum(u => u.Length) + 40; // a mitad del primer frame de vídeo
        var reader = new NutStreamReader(new MemoryStream(bytes[..cut]), pooled: false);
        for (int i = 0; i < firstVideo; i++) Assert.NotNull(await reader.ReadUnitAsync());
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadUnitAsync());
    }

    [Fact]
    public async Task CleanEndBetweenUnits_ReturnsNull()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        var units = await ReadAllAsync(new MemoryStream(bytes));
        int cut = units.Take(6).Sum(u => u.Length);
        var reader = new NutStreamReader(new MemoryStream(bytes[..cut]), pooled: false);
        for (int i = 0; i < 6; i++) Assert.NotNull(await reader.ReadUnitAsync());
        Assert.Null(await reader.ReadUnitAsync());
    }

    [Fact]
    public async Task NotNut_Throws()
    {
        var reader = new NutStreamReader(new MemoryStream("nut/multimedia containe\0XXXXXXXXXX"u8.ToArray()), pooled: false);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadUnitAsync());
    }

    [Fact]
    public async Task FrameBeforeMainHeader_Throws()
    {
        var bytes = "nut/multimedia container\0"u8.ToArray().Concat(new byte[] { 0x01, 0x02, 0x03 }).ToArray();
        var reader = new NutStreamReader(new MemoryStream(bytes), pooled: false);
        Assert.Equal(NutUnitKind.FileId, (await reader.ReadUnitAsync())!.Kind);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadUnitAsync());
    }

    [Theory]
    [InlineData(new byte[] { 0x05 }, 5UL, 1)]
    [InlineData(new byte[] { 0x81, 0xFF, 0x7F }, 32767UL, 3)]
    [InlineData(new byte[] { 0x85, 0x80, 0x00 }, 81920UL, 3)]
    public void Varint_Decodes(byte[] bytes, ulong expected, int consumed)
    {
        int pos = 0;
        Assert.True(NutStreamReader.TryReadVarint(bytes, ref pos, out var value));
        Assert.Equal(expected, value);
        Assert.Equal(consumed, pos);
    }

    [Fact]
    public void Varint_Incomplete_ReturnsFalse()
    {
        int pos = 0;
        Assert.False(NutStreamReader.TryReadVarint(new byte[] { 0x81, 0xFF }, ref pos, out _));
    }

    [Fact]
    public void Unit_ReleaseDropsTheBuffer()
    {
        var unit = new NutUnit(new byte[4], 4, pooled: false, NutUnitKind.Frame, 0, true, false);
        unit.Retain();
        unit.Release();
        Assert.Equal(4, unit.Bytes.Length);
        unit.Release();
        Assert.Throws<ObjectDisposedException>(() => unit.Bytes);
    }

    /// <summary>Entrega los bytes a trozos pequeños, como un socket perezoso.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(chunk, count), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
