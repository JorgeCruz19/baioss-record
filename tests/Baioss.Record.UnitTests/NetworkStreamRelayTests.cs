using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Relé loopback de las entradas de red: un consumidor nuevo recibe el pre-roll y luego el flujo en vivo sin duplicar
/// ni saltar bytes; al cerrarse el origen los consumidores ven EOF y el flujo siguiente arranca limpio; un consumidor
/// que no drena nunca bloquea al origen; y la ventana de pre-roll se acota por tiempo y por bytes. Con sockets reales
/// en loopback (es lo que FFmpeg usará), sin FFmpeg. El flujo son paquetes TS de 188 bytes (el relé solo reenvía
/// paquetes completos, porque realinea el audio dentro de ellos), en un PID ajeno al audio y al vídeo.
/// </summary>
public class NetworkStreamRelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const int Ts = TsAudioClockAligner.PacketSize;

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.NoDelay = true;
        return client;
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset), cts.Token);
            if (n <= 0) throw new EndOfStreamException($"EOF tras {offset} de {count} bytes.");
            offset += n;
        }
        return buffer;
    }

    private static async Task<int> ReadOnceAsync(NetworkStream stream, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        var buffer = new byte[64 * 1024];
        try { return await stream.ReadAsync(buffer, cts.Token); }
        catch (OperationCanceledException) { return -1; } // nada llegó en ese tiempo
    }

    /// <summary><paramref name="count"/> paquetes TS (PID 0x0FFF, sin cabecera PES) con relleno reconocible.</summary>
    private static byte[] TsPackets(int count, byte seed)
    {
        var bytes = new byte[count * Ts];
        for (int p = 0; p < count; p++)
        {
            int o = p * Ts;
            bytes[o] = 0x47; bytes[o + 1] = 0x0F; bytes[o + 2] = 0xFF; bytes[o + 3] = 0x10;
            for (int i = 4; i < Ts; i++) bytes[o + i] = (byte)(seed + p + i);
        }
        return bytes;
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ANewConsumer_GetsThePreroll_AndThenTheLiveStream_Contiguously()
    {
        await using var relay = new NetworkStreamRelay("test", NullLogger.Instance);
        relay.Start();

        using var source = await ConnectAsync(relay.SourcePort);
        var src = source.GetStream();
        var first = TsPackets(5, 1);
        await src.WriteAsync(first);
        await WaitAsync(() => relay.SourceBytes >= first.Length, "el relé no drenó el origen");
        Assert.True(relay.SourceConnected);

        using var consumer = await ConnectAsync(relay.ConsumerPort);
        var cs = consumer.GetStream();
        Assert.Equal(first, await ReadExactlyAsync(cs, first.Length)); // pre-roll: lo recibido ANTES de conectar

        var second = TsPackets(3, 7);
        await src.WriteAsync(second);
        Assert.Equal(second, await ReadExactlyAsync(cs, second.Length)); // en vivo, sin repetir el pre-roll ni saltar nada
        Assert.Equal(1, relay.ConsumerCount);
        Assert.Null(relay.AudioClockCorrectionSeconds); // sin PES de audio ni vídeo no hay nada que alinear
    }

    [Fact]
    public async Task WhenTheSourceCloses_ConsumersSeeEof_AndTheNextStreamStartsClean()
    {
        await using var relay = new NetworkStreamRelay("test", NullLogger.Instance);
        relay.Start();

        var source = await ConnectAsync(relay.SourcePort);
        var first = TsPackets(2, 3);
        await source.GetStream().WriteAsync(first);
        await WaitAsync(() => relay.SourceBytes >= first.Length, "el relé no drenó el origen");

        using var consumer = await ConnectAsync(relay.ConsumerPort);
        var cs = consumer.GetStream();
        await ReadExactlyAsync(cs, first.Length);

        source.Close(); // el emisor se fue: el receptor termina
        Assert.Equal(0, await ReadOnceAsync(cs, Timeout)); // EOF: FFmpeg finaliza el archivo y sale
        await WaitAsync(() => !relay.SourceConnected && relay.ConsumerCount == 0, "el relé no cerró al consumidor");

        // Un consumidor que conecta sin origen espera; el flujo siguiente le llega LIMPIO (sin el pre-roll viejo).
        using var consumer2 = await ConnectAsync(relay.ConsumerPort);
        var cs2 = consumer2.GetStream();
        await WaitAsync(() => relay.ConsumerCount == 1, "no se registró el consumidor");
        Assert.Equal(-1, await ReadOnceAsync(cs2, TimeSpan.FromMilliseconds(300)));

        using var source2 = await ConnectAsync(relay.SourcePort);
        var fresh = TsPackets(1, 9);
        await source2.GetStream().WriteAsync(fresh);
        Assert.Equal(fresh, await ReadExactlyAsync(cs2, fresh.Length));
        Assert.Equal(-1, await ReadOnceAsync(cs2, TimeSpan.FromMilliseconds(300))); // y nada más
    }

    [Fact]
    public async Task AConsumerThatDoesNotDrain_NeverBlocksTheSource()
    {
        await using var relay = new NetworkStreamRelay("test", NullLogger.Instance) { ConsumerQueueCapacity = 4 };
        relay.Start();
        bool overflow = false;
        relay.ConsumerOverflow += (_, _) => overflow = true;

        using var stuck = await ConnectAsync(relay.ConsumerPort); // conecta y NUNCA lee (disco atascado)
        await WaitAsync(() => relay.ConsumerCount == 1, "no se registró el consumidor");

        using var source = await ConnectAsync(relay.SourcePort);
        var src = source.GetStream();
        var chunk = TsPackets(348, 0); // ≈64 KiB de paquetes completos
        long total = 256L * chunk.Length; // ≈16 MiB
        var writing = Task.Run(async () =>
        {
            for (long sent = 0; sent < total; sent += chunk.Length) await src.WriteAsync(chunk);
        });
        Assert.Same(writing, await Task.WhenAny(writing, Task.Delay(Timeout))); // el origen nunca se frena
        await writing;
        await WaitAsync(() => relay.SourceBytes >= total, "el relé no drenó todo el origen");
        Assert.True(overflow); // y avisó de que descartó para ese consumidor
        Assert.True(relay.SourceConnected);
    }

    [Fact]
    public async Task ThePreroll_IsBoundedByTime_AndByBytes()
    {
        // Por tiempo: lo más viejo que la ventana se descarta al llegar el fragmento siguiente.
        await using (var relay = new NetworkStreamRelay("tiempo", NullLogger.Instance) { PrerollWindow = TimeSpan.FromMilliseconds(50) })
        {
            relay.Start();
            using var source = await ConnectAsync(relay.SourcePort);
            var src = source.GetStream();
            var old = TsPackets(5, 1);
            await src.WriteAsync(old);
            await WaitAsync(() => relay.SourceBytes >= old.Length, "el relé no drenó el origen");
            await Task.Delay(300);
            var recent = TsPackets(1, 5);
            await src.WriteAsync(recent);
            await WaitAsync(() => relay.SourceBytes >= old.Length + recent.Length, "el relé no drenó el origen");

            using var consumer = await ConnectAsync(relay.ConsumerPort);
            var cs = consumer.GetStream();
            Assert.Equal(recent, await ReadExactlyAsync(cs, recent.Length));
            Assert.Equal(-1, await ReadOnceAsync(cs, TimeSpan.FromMilliseconds(300)));
        }

        // Por bytes: con un tope de 250 bytes y dos paquetes de 188, solo sobrevive el último.
        await using (var relay = new NetworkStreamRelay("bytes", NullLogger.Instance) { PrerollMaxBytes = 250 })
        {
            relay.Start();
            using var source = await ConnectAsync(relay.SourcePort);
            var src = source.GetStream();
            await src.WriteAsync(TsPackets(1, 1));
            await WaitAsync(() => relay.SourceBytes >= Ts, "el relé no drenó el origen");
            var last = TsPackets(1, 5);
            await src.WriteAsync(last);
            await WaitAsync(() => relay.SourceBytes >= 2 * Ts, "el relé no drenó el origen");

            using var consumer = await ConnectAsync(relay.ConsumerPort);
            var cs = consumer.GetStream();
            Assert.Equal(last, await ReadExactlyAsync(cs, last.Length));
            Assert.Equal(-1, await ReadOnceAsync(cs, TimeSpan.FromMilliseconds(300)));
        }
    }
}
