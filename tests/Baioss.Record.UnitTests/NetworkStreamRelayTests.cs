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

    // --- Pre-roll alineado a fotograma clave (flujo con vídeo, como el que produce el receptor) ---

    private const int V = TsAudioClockAligner.VideoPid, A = TsAudioClockAligner.AudioPid;

    /// <summary>Paquete TS que abre un PES: vídeo (frame <paramref name="frame"/> a 30 fps, DTS = frame × 3000, con
    /// random_access_indicator si es keyframe, como lo escribe el muxer mpegts de FFmpeg) o audio (PTS dado).</summary>
    private static byte[] PesPacket(int pid, long dts, bool keyframe, int tag)
    {
        var p = new byte[Ts];
        Array.Fill(p, (byte)0xAB);
        p[0] = 0x47; p[1] = (byte)(0x40 | (pid >> 8)); p[2] = (byte)(pid & 0xFF);
        p[3] = 0x30; p[4] = 1; p[5] = (byte)(keyframe ? 0x40 : 0x00); // campo de adaptación de 1 byte: solo las banderas
        int o = 6;
        p[o] = 0; p[o + 1] = 0; p[o + 2] = 1; p[o + 3] = (byte)(pid == V ? 0xE0 : 0xC0);
        p[o + 4] = 0; p[o + 5] = 0; p[o + 6] = 0x80; p[o + 7] = 0xC0; p[o + 8] = 10;
        WriteTs(p, o + 9, dts + 3000, 0x3); // PTS un frame por delante
        WriteTs(p, o + 14, dts, 0x1);
        BitConverter.GetBytes(tag).CopyTo(p, 100);
        return p;
    }

    private static void WriteTs(byte[] p, int i, long ts, int marker)
    {
        p[i] = (byte)((marker << 4) | (int)((ts >> 29) & 0x0E) | 1);
        p[i + 1] = (byte)((ts >> 22) & 0xFF);
        p[i + 2] = (byte)(((ts >> 14) & 0xFE) | 1);
        p[i + 3] = (byte)((ts >> 7) & 0xFF);
        p[i + 4] = (byte)(((ts << 1) & 0xFE) | 1);
    }

    private static int Tag(byte[] p) => BitConverter.ToInt32(p, 100);

    /// <summary>Vídeo a 30 fps del frame <paramref name="from"/> al <paramref name="to"/> (excluido), keyframe cada
    /// <paramref name="gop"/> frames, con un PES de audio cada 4 frames (etiquetas: vídeo = frame, audio = 100000 + frame).</summary>
    private static byte[] VideoStream(int from, int to, int gop)
    {
        var bytes = new List<byte>();
        for (int i = from; i < to; i++)
        {
            bytes.AddRange(PesPacket(V, i * 3000L, keyframe: i % gop == 0, tag: i));
            if (i % 4 == 0) bytes.AddRange(PesPacket(A, i * 3000L + 100, keyframe: false, tag: 100_000 + i));
        }
        return bytes.ToArray();
    }

    private static async Task<byte[][]> ReadPacketsAsync(NetworkStream stream, int count)
    {
        var all = await ReadExactlyAsync(stream, count * Ts);
        return Enumerable.Range(0, count).Select(i => all[(i * Ts)..((i + 1) * Ts)]).ToArray();
    }

    [Fact]
    public async Task ThePreroll_StartsAtTheLatestKeyframe_ThatAlreadyHasTheWindowOfVideoBehindIt()
    {
        // GOP de 2 s (60 frames) y ventana de 2,5 s. Con 6,53 s de vídeo (frames 0–195), el keyframe de los 4 s ya tiene
        // 2,53 s por detrás; el de los 6 s no. Un consumidor nuevo recibe desde el keyframe de los 4 s (frame 120), no
        // desde «los últimos 2,5 s de llegada» (que empezarían a mitad de GOP) ni desde el keyframe más reciente.
        await using var relay = new NetworkStreamRelay("gop", NullLogger.Instance) { PrerollWindow = TimeSpan.FromSeconds(2.5) };
        relay.Start();
        using var source = await ConnectAsync(relay.SourcePort);
        var src = source.GetStream();
        var stream = VideoStream(0, 196, gop: 60);
        await src.WriteAsync(stream);
        await WaitAsync(() => relay.ForwardedBytes >= stream.Length, "el relé no drenó el origen");

        using var consumer = await ConnectAsync(relay.ConsumerPort);
        var cs = consumer.GetStream();
        int expected = stream.Length / Ts - (120 + 30); // desde el frame 120: 76 vídeo + 19 audio; antes iban 120 + 30
        var got = await ReadPacketsAsync(cs, expected);
        Assert.Equal(120, Tag(got[0]));                         // empieza justo en el keyframe de los 4 s…
        Assert.Equal(0x40, got[0][5] & 0x40);                    // …que lleva random_access_indicator
        Assert.Equal(Enumerable.Range(120, 76), got.Where(p => (((p[1] & 0x1F) << 8) | p[2]) == V).Select(Tag)); // vídeo contiguo
        Assert.Equal(-1, await ReadOnceAsync(cs, TimeSpan.FromMilliseconds(300)));           // y nada más (todo lo anterior se descartó)
    }

    [Fact]
    public async Task WithAGopLongerThanTheWindow_ThePrerollKeepsTheFirstKeyframe_ItHas()
    {
        // GOP de 8 s y solo 3 s de vídeo: ningún keyframe tiene aún 2,5 s por detrás salvo el primero (frame 0), que
        // los tiene → se conserva desde él. Descartarlo dejaría al proceso nuevo sin nada que decodificar hasta el keyframe siguiente.
        await using var relay = new NetworkStreamRelay("gop-largo", NullLogger.Instance) { PrerollWindow = TimeSpan.FromSeconds(2.5) };
        relay.Start();
        using var source = await ConnectAsync(relay.SourcePort);
        var src = source.GetStream();
        var stream = VideoStream(0, 90, gop: 240);
        await src.WriteAsync(stream);
        await WaitAsync(() => relay.ForwardedBytes >= stream.Length, "el relé no drenó el origen");

        using var consumer = await ConnectAsync(relay.ConsumerPort);
        var got = await ReadPacketsAsync(consumer.GetStream(), stream.Length / Ts);
        Assert.Equal(0, Tag(got[0]));
        Assert.Equal(stream, got.SelectMany(p => p).ToArray()); // íntegro, en orden

        // Y con solo 1,5 s de vídeo (menos que la ventana) también: el único keyframe manda.
        await using var young = new NetworkStreamRelay("joven", NullLogger.Instance) { PrerollWindow = TimeSpan.FromSeconds(2.5) };
        young.Start();
        using var source2 = await ConnectAsync(young.SourcePort);
        var short45 = VideoStream(0, 45, gop: 60);
        await source2.GetStream().WriteAsync(short45);
        await WaitAsync(() => young.ForwardedBytes >= short45.Length, "el relé no drenó el origen");
        using var consumer2 = await ConnectAsync(young.ConsumerPort);
        Assert.Equal(short45, (await ReadPacketsAsync(consumer2.GetStream(), short45.Length / Ts)).SelectMany(p => p).ToArray());
    }

    [Fact]
    public async Task AReservedConsumer_GetsTheSnapshotOfTheReservation_PlusEverythingSince_AndTheFrameCountIsExact()
    {
        // El motor reserva al construir el proceso y este conecta unos cientos de ms después: la instantánea es la del
        // momento de reservar (el número de frames que el preview se salta es exacto) y lo llegado entre medias viaja en
        // su cola, sin hueco ni solape.
        await using var relay = new NetworkStreamRelay("reserva", NullLogger.Instance) { PrerollWindow = TimeSpan.FromSeconds(2.5) };
        relay.Start();
        using var source = await ConnectAsync(relay.SourcePort);
        var src = source.GetStream();
        var first = VideoStream(0, 90, gop: 60); // 3 s: la ventana arranca en el keyframe 0 (el de los 2 s aún no tiene 2,5 s detrás)
        await src.WriteAsync(first);
        await WaitAsync(() => relay.ForwardedBytes >= first.Length, "el relé no drenó el origen");

        Assert.Equal(90, relay.ReserveConsumer().VideoFrames); // los PES de vídeo de la instantánea
        var meanwhile = VideoStream(90, 120, gop: 60);   // llega ANTES de que el proceso conecte
        await src.WriteAsync(meanwhile);
        await WaitAsync(() => relay.ForwardedBytes >= first.Length + meanwhile.Length, "el relé no drenó el origen");

        using var reserved = await ConnectAsync(relay.ConsumerPort);
        var cs = reserved.GetStream();
        var got = await ReadPacketsAsync(cs, (first.Length + meanwhile.Length) / Ts);
        Assert.Equal(first.Concat(meanwhile).ToArray(), got.SelectMany(p => p).ToArray());

        var live = VideoStream(120, 142, gop: 60);       // y sigue en vivo
        await src.WriteAsync(live);
        await WaitAsync(() => relay.ForwardedBytes >= first.Length + meanwhile.Length + live.Length, "el relé no drenó el origen");
        Assert.Equal(live, (await ReadPacketsAsync(cs, live.Length / Ts)).SelectMany(p => p).ToArray());

        // Un consumidor SIN reserva recibe la ventana de AHORA: desde el keyframe de los 2 s (frame 60), que ya tiene 2,7 s detrás.
        using var plain = await ConnectAsync(relay.ConsumerPort);
        Assert.Equal(60, Tag((await ReadPacketsAsync(plain.GetStream(), 1))[0]));
    }

    [Fact]
    public async Task AReservation_IsLive_OnlyWhileItsConsumerHasTakenThePreroll_AndKeepsUpWithTheStream()
    {
        // El motor cede el preview al proceso nuevo cuando este ya lee en directo: conectó, tragó el pre-roll y no tiene
        // nada pendiente en su cola. Si se atrasa (el origen empuja más de lo que lee), deja de estar en directo hasta
        // que vacía el atraso.
        await using var relay = new NetworkStreamRelay("directo", NullLogger.Instance) { PrerollWindow = TimeSpan.FromSeconds(2.5), ConsumerQueueCapacity = 4096 };
        relay.Start();
        using var source = await ConnectAsync(relay.SourcePort);
        var src = source.GetStream();
        var first = VideoStream(0, 90, gop: 60);
        await src.WriteAsync(first);
        await WaitAsync(() => relay.ForwardedBytes >= first.Length, "el relé no drenó el origen");

        var reservation = relay.ReserveConsumer();
        Assert.Equal(90, reservation.VideoFrames);
        Assert.False(reservation.IsLive);                       // nadie ha conectado aún

        using var consumer = await ConnectAsync(relay.ConsumerPort);
        var cs = consumer.GetStream();
        await ReadPacketsAsync(cs, first.Length / Ts);           // se traga el pre-roll entero
        await WaitAsync(() => reservation.IsLive, "el consumidor al día no se reporta en directo");

        // Deja de leer mientras el origen sigue empujando (más de lo que caben los búferes del socket): el atraso se
        // acumula en su cola → ya no va al día.
        var backlog = TsPackets(8000, 7);                        // ~1,5 MB
        await src.WriteAsync(backlog);
        await WaitAsync(() => relay.ForwardedBytes >= first.Length + backlog.Length, "el relé no drenó el origen");
        Assert.False(reservation.IsLive);

        await ReadExactlyAsync(cs, backlog.Length);              // vacía el atraso: al día otra vez
        await WaitAsync(() => reservation.IsLive, "el consumidor no volvió a estar en directo tras vaciar la cola");
    }

    [Fact]
    public async Task AReservation_NobodyClaims_ExpiresWithoutLeakingAConsumer()
    {
        await using var relay = new NetworkStreamRelay("caduca", NullLogger.Instance) { ReservationTimeout = TimeSpan.FromMilliseconds(100) };
        relay.Start();
        using var source = await ConnectAsync(relay.SourcePort);
        var src = source.GetStream();
        await src.WriteAsync(VideoStream(0, 30, gop: 30));
        await WaitAsync(() => relay.ForwardedBytes >= 30 * Ts, "el relé no drenó el origen");

        var reservation = relay.ReserveConsumer();
        Assert.Equal(30, reservation.VideoFrames);
        Assert.Equal(1, relay.ConsumerCount);            // reservado = ya cuenta (recibe el flujo en su cola)
        Assert.False(reservation.IsLive);                // nadie ha conectado: no está en directo
        await Task.Delay(300);
        await src.WriteAsync(VideoStream(30, 31, gop: 30)); // el fragmento siguiente caduca la reserva
        await WaitAsync(() => relay.ConsumerCount == 0, "la reserva no caducó");
        Assert.True(reservation.IsLive);                 // caducada: no hay nada que esperar de ella

        using var plain = await ConnectAsync(relay.ConsumerPort); // sin reserva pendiente: instantánea de ahora
        var head = await ReadPacketsAsync(plain.GetStream(), 1);
        Assert.Equal(0, Tag(head[0]));
    }

    [Fact]
    public async Task WhenTheByteCapBites_ThePrerollDropsTheOldest_AndRestartsAtTheNextKeyframe()
    {
        // Tope de 50 paquetes con keyframes cada 20 frames y ventana de flujo pequeña (100 ms): tras 100 frames (125
        // paquetes con el audio) sobreviven como mucho 50 → la ventana arranca en el keyframe siguiente al corte (frame 80).
        await using var relay = new NetworkStreamRelay("tope", NullLogger.Instance) { PrerollWindow = TimeSpan.FromMilliseconds(100), PrerollMaxBytes = 50 * Ts };
        relay.Start();
        using var source = await ConnectAsync(relay.SourcePort);
        var src = source.GetStream();
        var stream = VideoStream(0, 100, gop: 20);
        await src.WriteAsync(stream);
        await WaitAsync(() => relay.ForwardedBytes >= stream.Length, "el relé no drenó el origen");

        using var consumer = await ConnectAsync(relay.ConsumerPort);
        var cs = consumer.GetStream();
        var got = await ReadPacketsAsync(cs, 20 + 5); // frames 80–99 y sus 5 PES de audio
        Assert.Equal(80, Tag(got[0]));
        Assert.Equal(0x40, got[0][5] & 0x40);
        Assert.Equal(-1, await ReadOnceAsync(cs, TimeSpan.FromMilliseconds(300)));
    }
}
