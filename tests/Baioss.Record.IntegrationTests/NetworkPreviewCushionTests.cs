using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Colchón de preview de una entrada de red: una señal que LLEGA A RÁFAGAS (aquí, un proxy TCP que retiene el flujo RTMP
/// y lo suelta de golpe cada 800 ms, como un servidor que entrega a trompicones) se ve a saltos sin colchón y a cadencia
/// constante con él, con el motor real de principio a fin (receptor → relé → proceso del canal → colchón → FrameReady).
/// Además, una sonda opcional contra una URL real (BAIOSS_LIVE_RTMP_URL) que mide y deja constancia sin juzgar.
/// </summary>
public sealed class NetworkPreviewCushionTests
{
    private readonly ITestOutputHelper _out;
    public NetworkPreviewCushionTests(ITestOutputHelper output) { _out = output; }

    private static RecordingProfile Profile() => new()
    {
        Name = "net", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(2), GopSize = 30,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    /// <summary>Proxy TCP loopback que retiene lo que va del servidor al cliente y lo suelta de golpe cada <paramref name="burst"/>.</summary>
    private sealed class BurstyProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _target;
        private readonly TimeSpan _burst;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _pumps = new();
        public BurstyProxy(int target, TimeSpan burst)
        {
            _target = target; _burst = burst;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }
        public int Port { get; }
        public long HeldBytes { get; private set; }
        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                var server = new TcpClient();
                await server.ConnectAsync(IPAddress.Loopback, _target, _cts.Token);
                client.NoDelay = true; server.NoDelay = true;
                lock (_pumps)
                {
                    _pumps.Add(PumpAsync(client.GetStream(), server.GetStream(), TimeSpan.Zero)); // cliente → servidor: directo
                    _pumps.Add(PumpAsync(server.GetStream(), client.GetStream(), _burst));       // servidor → cliente: a ráfagas
                }
            }
        }
        private async Task PumpAsync(NetworkStream from, NetworkStream to, TimeSpan hold)
        {
            var buffer = new byte[64 * 1024];
            var held = new MemoryStream();
            var sw = Stopwatch.StartNew();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    if (hold > TimeSpan.Zero) readCts.CancelAfter(50); // lee a trozos para poder soltar la ráfaga a su hora
                    int n;
                    try { n = await from.ReadAsync(buffer, readCts.Token); }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { n = -1; }
                    if (n == 0) break;
                    if (n > 0)
                    {
                        if (hold == TimeSpan.Zero) { await to.WriteAsync(buffer.AsMemory(0, n), _cts.Token); continue; }
                        held.Write(buffer, 0, n);
                        HeldBytes += n;
                    }
                    if (hold > TimeSpan.Zero && sw.Elapsed >= hold && held.Length > 0)
                    {
                        await to.WriteAsync(held.GetBuffer().AsMemory(0, (int)held.Length), _cts.Token);
                        await to.FlushAsync(_cts.Token);
                        held.SetLength(0);
                        sw.Restart();
                    }
                }
            }
            catch { /* cierre */ }
            finally { try { to.Dispose(); } catch { } }
        }
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            Task[] pumps; lock (_pumps) pumps = _pumps.ToArray();
            try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
    }

    /// <summary>Arranca el motor sobre la entrada dada, espera SEÑAL OK, descarta <paramref name="settle"/> y mide FrameReady durante <paramref name="measure"/>.</summary>
    private static async Task<(double[] Frames, (int Queued, long Delivered, long Repeated, long Dropped) Cushion)> MeasureAsync(
        FfmpegLocator locator, InputSource input, TimeSpan settle, TimeSpan measure)
    {
        var factory = new NetworkStreamCaptureSourceFactory(locator, NullLoggerFactory.Instance);
        await using var source = (NetworkStreamCaptureSource)factory.Create(input);
        await source.OpenAsync();
        await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = Path.Combine(Path.GetTempPath(), $"baioss-cushion-{Guid.NewGuid():N}") };
        var sw = Stopwatch.StartNew();
        var frames = new List<double>();
        engine.FrameReady += (_, _) => { lock (frames) frames.Add(sw.Elapsed.TotalSeconds); };
        await engine.StartPreviewAsync(source, Profile(), "NET");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (source.CurrentSignal.State != SignalState.Locked && DateTime.UtcNow < deadline) await Task.Delay(250);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        await Task.Delay(settle);
        double from = sw.Elapsed.TotalSeconds;
        await Task.Delay(measure);
        double to = sw.Elapsed.TotalSeconds;
        double[] f; lock (frames) f = frames.Where(t => t >= from && t <= to).Select(t => t - from).ToArray();
        return (f, engine.PreviewCushion);
    }

    private static string Summary(double[] f, double seconds)
    {
        if (f.Length < 2) return $"{f.Length} frames";
        var gaps = f.Zip(f.Skip(1), (a, b) => b - a).ToArray();
        var perSecond = Enumerable.Range(0, (int)seconds).Select(s => f.Count(t => t >= s && t < s + 1)).ToArray();
        return string.Create(CultureInfo.InvariantCulture,
            $"{f.Length} frames en {seconds:0} s ({f.Length / seconds:0.0} fps) · hueco máx {gaps.Max():0.000} s · huecos >0,2 s: {gaps.Count(g => g > 0.2)} · >0,5 s: {gaps.Count(g => g > 0.5)} · >1 s: {gaps.Count(g => g > 1)} · frames/s mín {perSecond.Min()} máx {perSecond.Max()}");
    }

    [SkippableFact]
    public async Task WithAPreviewBuffer_ABurstyRtmpSource_IsPreviewedAtASteadyCadence()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        const int senderPort = 19353;
        await using var proxy = new BurstyProxy(senderPort, TimeSpan.FromMilliseconds(800));
        InputSource Input(int cushionMs) => new NetworkInput
        {
            Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = "127.0.0.1", Port = proxy.Port, Path = "live/test",
            PreviewBufferMs = cushionMs,
        }.ToInputSource(Guid.NewGuid(), $"rtmp a ráfagas ({cushionMs} ms)");
        var settle = TimeSpan.FromSeconds(4);
        var measure = TimeSpan.FromSeconds(10);
        // Un emisor por fase: el servidor RTMP de FFmpeg (-listen 1) atiende a UN cliente y termina cuando este se va.
        async Task<(double[] Frames, (int Queued, long Delivered, long Repeated, long Dropped) Cushion)> PhaseAsync(int cushionMs)
        {
            using var sender = NetworkPreviewContinuityTests.LightSender(senderPort);
            try
            {
                await Task.Delay(1500);
                return await MeasureAsync(locator, Input(cushionMs), settle, measure);
            }
            finally
            {
                try { if (!sender.HasExited) sender.Kill(entireProcessTree: true); } catch { /* ya salió */ }
            }
        }

        // Sin colchón: las ráfagas del proxy llegan tal cual al preview (huecos de ~0,8 s).
        var (bare, _) = await PhaseAsync(0);
        _out.WriteLine("sin colchón:  " + Summary(bare, measure.TotalSeconds));
        var bareGaps = bare.Zip(bare.Skip(1), (a, b) => b - a).ToArray();
        Assert.True(bareGaps.Count(g => g > 0.5) >= 5, "el banco no produjo ráfagas: " + Summary(bare, measure.TotalSeconds));

        // Con colchón de 1,2 s: cadencia constante (30 fps nominales: 15 por medio segundo) sin huecos ni ráfagas.
        var (cushioned, stats) = await PhaseAsync(1200);
        _out.WriteLine($"con colchón:  {Summary(cushioned, measure.TotalSeconds)} · en cola {stats.Queued} · repetidos {stats.Repeated} · descartados {stats.Dropped}");
        var gaps = cushioned.Zip(cushioned.Skip(1), (a, b) => b - a).ToArray();
        Assert.True(gaps.Max() < 0.2, "hueco con colchón: " + Summary(cushioned, measure.TotalSeconds));
        var halves = Enumerable.Range(0, 20).Select(i => cushioned.Count(t => t >= i * 0.5 && t < (i + 1) * 0.5)).ToArray();
        Assert.True(halves.Min() >= 11 && halves.Max() <= 19, "cadencia con colchón por medio segundo: " + string.Join(" ", halves));
        Assert.True(stats.Dropped <= 5, $"descartes con colchón: {stats.Dropped} (la ráfaga de 0,8 s debe caber en 1,2 s)");
    }

    /// <summary>Sonda manual: BAIOSS_LIVE_RTMP_URL (URL real, p. ej. rtmp://servidor:puerto) y BAIOSS_LIVE_PREVIEW_BUFFER_MS (0 =
    /// sin colchón). Mide 40 s de FrameReady y escribe el resumen en la salida del test y en BAIOSS_LIVE_REPORT si está definida.</summary>
    [SkippableFact]
    public async Task LiveProbe_MeasuresThePreviewCadence_OfARealSource()
    {
        string? url = Environment.GetEnvironmentVariable("BAIOSS_LIVE_RTMP_URL");
        Skip.If(string.IsNullOrWhiteSpace(url) || TestAssets.FfmpegDir is null, "sonda manual: define BAIOSS_LIVE_RTMP_URL");
        Assert.True(NetworkInput.TryParseUrl(url!, out var parsed, out var error) && parsed is not null, $"URL: {error}");
        int cushion = int.TryParse(Environment.GetEnvironmentVariable("BAIOSS_LIVE_PREVIEW_BUFFER_MS"), out var c) ? c : 0;
        var input = (parsed! with { PreviewBufferMs = cushion }).ToInputSource(Guid.NewGuid(), "sonda");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);

        var measure = TimeSpan.FromSeconds(40);
        var (frames, stats) = await MeasureAsync(locator, input, TimeSpan.FromSeconds(6), measure);
        string line = $"colchón {cushion} ms: {Summary(frames, measure.TotalSeconds)} · en cola {stats.Queued} · repetidos {stats.Repeated} · descartados {stats.Dropped}";
        _out.WriteLine(line);
        if (Environment.GetEnvironmentVariable("BAIOSS_LIVE_REPORT") is { Length: > 0 } report) File.AppendAllText(report, line + Environment.NewLine);
        Assert.True(frames.Length > 0, "la fuente no entregó frames");
    }
}
