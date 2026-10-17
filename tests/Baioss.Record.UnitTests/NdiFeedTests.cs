using System.Net;
using System.Net.Sockets;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Flujo de un receptor NDI hacia el proceso de FFmpeg conectado (<see cref="NdiFeed{T}"/>), con sockets loopback de verdad. Lo
/// importante es el FIN DE FLUJO: al retirar el proceso que graba, recibe exactamente lo encolado hasta ese instante y luego
/// EOF —antes se le cortaba con la «q» y el audio que aún no había leído se perdía—, y lo que llega después es para el
/// proceso siguiente.
/// </summary>
public sealed class NdiFeedTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clients = new();
    private int _released;

    public NdiFeedTests() => _listener.Start();

    private NdiFeed<byte[]> NewFeed(int capacity) => new(capacity, b => b, _ => Interlocked.Increment(ref _released));

    /// <summary>Conecta un «FFmpeg» al flujo: devuelve el extremo que lee.</summary>
    private async Task<NetworkStream> ConnectAsync(NdiFeed<byte[]> feed)
    {
        var reader = new TcpClient();
        await reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        var served = await _listener.AcceptTcpClientAsync();
        _clients.Add(reader);
        feed.Attach(served);
        return reader.GetStream();
    }

    /// <summary>Un elemento de <paramref name="size"/> bytes que lleva su número en todos ellos.</summary>
    private static byte[] Item(int n, int size = 1000) => Enumerable.Repeat((byte)n, size).ToArray();

    /// <summary>Lee hasta EOF (con tope de tiempo) y devuelve los números de los elementos recibidos, en orden.</summary>
    private static async Task<List<int>> ReadToEndAsync(NetworkStream s, int size = 1000)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var all = new MemoryStream();
        var buf = new byte[64 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, timeout.Token)) > 0) all.Write(buf, 0, n);
        var bytes = all.ToArray();
        Assert.Equal(0, bytes.Length % size);
        return Enumerable.Range(0, bytes.Length / size).Select(i => (int)bytes[i * size]).ToList();
    }

    private static async Task<List<int>> ReadItemsAsync(NetworkStream s, int count, int size = 1000)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buf = new byte[count * size];
        await s.ReadExactlyAsync(buf, timeout.Token);
        return Enumerable.Range(0, count).Select(i => (int)buf[i * size]).ToList();
    }

    [Fact]
    public async Task End_GivesTheConnectedProcessWhatWasQueued_ThenEof_AndWhatComesLaterGoesToTheNextOne()
    {
        await using var feed = NewFeed(64);
        var first = await ConnectAsync(feed);
        for (int i = 1; i <= 10; i++) feed.Enqueue(Item(i));

        Assert.True(feed.End());
        for (int i = 11; i <= 15; i++) feed.Enqueue(Item(i)); // después del fin: no son del primero (nadie los espera aún)

        Assert.Equal(Enumerable.Range(1, 10), await ReadToEndAsync(first)); // todo lo de antes, y EOF
        // Cada elemento se suelta una sola vez, escrito o descartado: los 11–15 no tenían a quién ir.
        await WaitUntilAsync(() => Volatile.Read(ref _released) == 15);

        var second = await ConnectAsync(feed);
        for (int i = 20; i <= 22; i++) feed.Enqueue(Item(i));
        Assert.Equal(new[] { 20, 21, 22 }, await ReadItemsAsync(second, 3));
        await WaitUntilAsync(() => Volatile.Read(ref _released) == 18);
    }

    [Fact]
    public async Task End_DeliversWhatWasStillQueued_WhileTheProcessWasNotReading()
    {
        // El caso de FFmpeg: no lee el audio hasta haber vaciado el codificador de vídeo. Lo encolado tras llenarse el socket
        // tiene que llegarle igual, y lo que se encola mientras tanto no puede desplazarlo de la cola.
        const int size = 256 * 1024, count = 40; // 10 MB: más de lo que cabe en los búferes del socket
        await using var feed = NewFeed(count);
        var reader = await ConnectAsync(feed);
        for (int i = 1; i <= count; i++) feed.Enqueue(Item(i, size));
        await Task.Delay(200);

        Assert.True(feed.End());
        for (int i = 0; i < 2 * count; i++) feed.Enqueue(Item(200, size)); // la cola nueva, no la del proceso que termina

        Assert.Equal(Enumerable.Range(1, count), await ReadToEndAsync(reader, size));
    }

    [Fact]
    public async Task End_WithoutAConnectedProcess_SaysSo()
    {
        await using var feed = NewFeed(4);
        feed.Enqueue(Item(1));
        Assert.False(feed.End());
    }

    [Fact]
    public async Task Attach_ServesTheNewProcessAndClosesThePreviousOne()
    {
        // Un proceso relanzado conecta otra vez sin fin de flujo: desde ese momento todo es suyo, y el socket anterior se cierra
        // (antes cada reconexión dejaba el suyo abierto: handles y puertos acumulados en 24/7).
        await using var feed = NewFeed(16);
        var first = await ConnectAsync(feed);
        feed.Enqueue(Item(1));
        Assert.Equal(new[] { 1 }, await ReadItemsAsync(first, 1));

        var second = await ConnectAsync(feed);
        feed.Enqueue(Item(2));
        Assert.Equal(new[] { 2 }, await ReadItemsAsync(second, 1));
        Assert.Empty(await ReadToEndAsync(first));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "La condición no se cumplió a tiempo.");
            await Task.Delay(20);
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
