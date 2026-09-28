using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Relé loopback de una captura en crudo (NUT): un consumidor conectado antes del flujo lo recibe entero; uno que llega a
/// mitad recibe primero las cabeceras y arranca en el siguiente punto de sincronía, sin duplicar ni saltar bytes; al
/// cerrarse el origen los consumidores ven EOF y el flujo siguiente arranca con sus propias cabeceras; una reserva sabe
/// cuándo su proceso va al día y caduca si nadie la reclama; y un consumidor que no drena nunca bloquea al origen. Con
/// sockets reales en loopback (es lo que FFmpeg usará) y un NUT real del FFmpeg empaquetado (el fixture), sin FFmpeg.
/// </summary>
public class RawStreamRelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.nut");

    private sealed record Unit(NutUnitKind Kind, int Offset, int Length, bool Video);

    private static async Task<List<Unit>> UnitsAsync(byte[] bytes)
    {
        var reader = new NutStreamReader(new MemoryStream(bytes), pooled: false);
        var list = new List<Unit>();
        int offset = 0;
        while (await reader.ReadUnitAsync() is { } u) { list.Add(new Unit(u.Kind, offset, u.Length, u.IsVideoFrame)); offset += u.Length; }
        return list;
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.NoDelay = true;
        return client;
    }

    private static async Task<byte[]> ReadToEndAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var all = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int n = await stream.ReadAsync(buffer, cts.Token);
            if (n <= 0) return all.ToArray();
            all.Write(buffer, 0, n);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Esperando: " + what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ConsumerConnectedBeforeTheSource_ReceivesTheWholeStream()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        await using var relay = new RawStreamRelay("t", NullLogger.Instance);
        relay.Start();
        using var consumer = await ConnectAsync(relay.ConsumerPort);
        await WaitUntilAsync(() => relay.ConsumerCount == 1, "alta del consumidor");
        using (var source = await ConnectAsync(relay.SourcePort))
        {
            await source.GetStream().WriteAsync(bytes);
        }
        var got = await ReadToEndAsync(consumer.GetStream());
        Assert.Equal(bytes, got);
        Assert.Equal(5, relay.VideoFramesForwarded);
        Assert.Equal(bytes.Length, relay.SourceBytes);
    }

    [Fact]
    public async Task ConsumerConnectedMidStream_GetsTheHeaders_AndStartsAtTheNextSyncpoint()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        var units = await UnitsAsync(bytes);
        int firstSync = units.FindIndex(u => u.Kind == NutUnitKind.Syncpoint);
        int secondSync = units.FindIndex(firstSync + 1, u => u.Kind == NutUnitKind.Syncpoint);
        int cut = units[secondSync].Offset;

        await using var relay = new RawStreamRelay("t", NullLogger.Instance);
        relay.Start();
        using var source = await ConnectAsync(relay.SourcePort);
        await source.GetStream().WriteAsync(bytes.AsMemory(0, cut));
        await WaitUntilAsync(() => relay.ForwardedBytes >= cut, "el relé reparta la primera parte");

        using var consumer = await ConnectAsync(relay.ConsumerPort);
        await WaitUntilAsync(() => relay.ConsumerCount == 1, "alta del consumidor");
        await source.GetStream().WriteAsync(bytes.AsMemory(cut));
        source.Close();

        var got = await ReadToEndAsync(consumer.GetStream());
        var expected = bytes[..units[firstSync].Offset].Concat(bytes[cut..]).ToArray(); // cabeceras + desde el 2.º punto de sincronía
        Assert.Equal(expected, got);
        // Y lo recibido es un NUT que un lector entiende: cabeceras, luego un punto de sincronía y frames.
        var received = await UnitsAsync(got);
        Assert.Equal(NutUnitKind.FileId, received[0].Kind);
        Assert.Equal(NutUnitKind.Syncpoint, received[firstSync].Kind);
    }

    [Fact]
    public async Task ReservedConsumer_IsLiveOnceItHasCaughtUp()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        await using var relay = new RawStreamRelay("t", NullLogger.Instance);
        relay.Start();
        var reservation = relay.ReserveConsumer();
        Assert.False(reservation.IsLive); // sin conectar aún
        Assert.Equal(1, relay.ConsumerCount);

        using var consumer = await ConnectAsync(relay.ConsumerPort);
        await WaitUntilAsync(() => relay.ConsumerCount == 1 && !reservation.IsLive, "el consumidor reclame la reserva");
        Assert.False(reservation.IsLive); // conectado, pero sin flujo aún: no ha arrancado

        using var source = await ConnectAsync(relay.SourcePort);
        await source.GetStream().WriteAsync(bytes);
        var reading = ReadToEndAsync(consumer.GetStream());
        await WaitUntilAsync(() => reservation.IsLive, "el consumidor reservado vaya al día");
        source.Close();
        Assert.Equal(bytes, await reading); // conectado antes del flujo: lo recibe entero
    }

    [Fact]
    public async Task ExpiredReservation_IsDropped_AndCountsAsLive()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        await using var relay = new RawStreamRelay("t", NullLogger.Instance) { ReservationTimeout = TimeSpan.FromMilliseconds(100) };
        relay.Start();
        var reservation = relay.ReserveConsumer();
        await Task.Delay(300);
        using var source = await ConnectAsync(relay.SourcePort);
        await source.GetStream().WriteAsync(bytes);
        await WaitUntilAsync(() => relay.ConsumerCount == 0, "caduque la reserva");
        Assert.True(reservation.IsLive);
    }

    [Fact]
    public async Task SlowConsumer_NeverBlocksTheSource_AndOverflowIsReported()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        await using var relay = new RawStreamRelay("t", NullLogger.Instance) { ConsumerQueueMaxBytes = 2048 };
        relay.Start();
        int overflows = 0;
        relay.ConsumerOverflow += (_, _) => Interlocked.Increment(ref overflows);
        using var consumer = await ConnectAsync(relay.ConsumerPort); // nunca lee
        await WaitUntilAsync(() => relay.ConsumerCount == 1, "alta del consumidor");
        // Un flujo largo y VÁLIDO: las cabeceras del fixture, su cuerpo (puntos de sincronía + frames) repetido 100 veces y el
        // índice (dos archivos NUT pegados no son un flujo: la identificación solo va al principio).
        var units = await UnitsAsync(bytes);
        int firstSync = units.FindIndex(u => u.Kind == NutUnitKind.Syncpoint);
        int index = units[^1].Offset;
        var stream = new MemoryStream();
        stream.Write(bytes, 0, units[firstSync].Offset);
        for (int i = 0; i < 100; i++) stream.Write(bytes, units[firstSync].Offset, index - units[firstSync].Offset);
        stream.Write(bytes, index, bytes.Length - index);
        var longStream = stream.ToArray();
        using var source = await ConnectAsync(relay.SourcePort);
        using var cts = new CancellationTokenSource(Timeout);
        await source.GetStream().WriteAsync(longStream, cts.Token); // 1,9 MB: el origen nunca se bloquea
        await WaitUntilAsync(() => relay.SourceBytes >= longStream.Length, "el relé drene todo");
        Assert.True(overflows > 0, "el desbordamiento del consumidor lento no se avisó");
        Assert.True(relay.SourceConnected, "el relé cortó al origen por culpa del consumidor lento");
    }

    [Fact]
    public async Task SourceDisconnect_ClosesConsumers_AndTheNextStreamStartsFromItsOwnHeaders()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture);
        var units = await UnitsAsync(bytes);
        int cut = units.First(u => u.Video).Offset + units.First(u => u.Video).Length; // tras el primer frame de vídeo

        await using var relay = new RawStreamRelay("t", NullLogger.Instance);
        relay.Start();
        using var first = await ConnectAsync(relay.ConsumerPort);
        await WaitUntilAsync(() => relay.ConsumerCount == 1, "alta del primer consumidor");
        using (var source = await ConnectAsync(relay.SourcePort))
        {
            await source.GetStream().WriteAsync(bytes.AsMemory(0, cut));
        }
        Assert.Equal(bytes[..cut], await ReadToEndAsync(first.GetStream())); // EOF al irse el origen
        await WaitUntilAsync(() => relay.ConsumerCount == 0, "baja del primer consumidor");

        using var second = await ConnectAsync(relay.ConsumerPort);
        await WaitUntilAsync(() => relay.ConsumerCount == 1, "alta del segundo consumidor");
        using (var source = await ConnectAsync(relay.SourcePort))
        {
            await source.GetStream().WriteAsync(bytes);
        }
        Assert.Equal(bytes, await ReadToEndAsync(second.GetStream())); // el flujo nuevo, desde sus cabeceras, sin restos del anterior
    }

    [Fact]
    public async Task InvalidStream_CutsTheSource_AndConsumersSeeEof()
    {
        await using var relay = new RawStreamRelay("t", NullLogger.Instance);
        relay.Start();
        using var consumer = await ConnectAsync(relay.ConsumerPort);
        await WaitUntilAsync(() => relay.ConsumerCount == 1, "alta del consumidor");
        using var source = await ConnectAsync(relay.SourcePort);
        await source.GetStream().WriteAsync("esto no es NUT ni de lejos, son bytes cualesquiera"u8.ToArray());
        var got = await ReadToEndAsync(consumer.GetStream()); // el relé corta: EOF sin haber repartido nada
        Assert.Empty(got);
        await WaitUntilAsync(() => !relay.SourceConnected, "el relé cierre al origen");
    }
}
