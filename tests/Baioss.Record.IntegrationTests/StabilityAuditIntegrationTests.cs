using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Auditoría de estabilidad 2026-09, con FFmpeg real: (1) una caída del PROCESO de grabación con la señal presente
/// reconstruye la fuente viva y NO graba barras (antes, con una fuente que publica su señal, se quedaba en barras hasta que
/// la señal «volviera», y la señal nunca se había ido); (2) una captura que deja de entregar frames sin decir nada se
/// reabre sola (antes FFmpeg seguía imprimiendo progreso con el contador parado y nadie lo detectaba).
/// </summary>
public sealed class StabilityAuditIntegrationTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Esperando: " + what);
            await Task.Delay(50);
        }
    }

    [SkippableFact]
    public async Task RecorderDeathWithTheSignalPresent_RebuildsTheLiveSource_NotBars()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = Path.Combine(Path.GetTempPath(), $"baioss-recover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var (source, _) = DecklinkRelayContinuityTests.SyntheticSource(locator, DecklinkRelayContinuityTests.SyntheticDevice(640, 360, 25));
        try
        {
            await source.OpenAsync();
            await using var engine = new FfmpegChannelEngine(locator, NullLogger<FfmpegChannelEngine>.Instance) { OutputRoot = dir };
            int frames = 0;
            engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);
            await engine.StartPreviewAsync(source, DecklinkRelayContinuityTests.Profile(), "R");
            await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.Locked && Volatile.Read(ref frames) >= 25, TimeSpan.FromSeconds(20), "preview");

            var profile = DecklinkRelayContinuityTests.Profile();
            profile.SlateOnSignalLoss = true; // con carta de ajuste: la caída de antes acababa en barras
            await engine.StartRecordingAsync(Guid.NewGuid(), profile);
            await Task.Delay(3000);
            bool sawSlate = false;
            using var watch = new CancellationTokenSource();
            var watcher = Task.Run(async () => { while (!watch.IsCancellationRequested) { if (engine.IsSlate) sawSlate = true; await Task.Delay(20); } });

            Assert.True(engine.KillRecorderProcessForTest(), "no había proceso de grabación que matar");
            // Pieza nueva de la fuente viva (backoff de 1 s + arranque), sin pasar por barras.
            await WaitUntilAsync(() => MediaFiles(dir).Length >= 2, TimeSpan.FromSeconds(20), "pieza nueva");
            await Task.Delay(3000);
            watch.Cancel();
            await watcher;
            Assert.False(sawSlate, "la caída del proceso con señal presente mandó el canal a barras");
            Assert.Equal(RecordingState.Recording, engine.State);
            await engine.StopRecordingAsync();
            var files = MediaFiles(dir);
            Assert.Equal(2, files.Length);
            Assert.All(files, f => Assert.True(new FileInfo(f).Length > 50_000, $"{Path.GetFileName(f)} demasiado pequeño"));
        }
        finally
        {
            await source.DisposeAsync();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Los .mp4 de la carpeta, sin los temporales de la optimización (remux faststart) que pueda haber en vuelo.</summary>
    private static string[] MediaFiles(string dir) =>
        Directory.GetFiles(dir, "*.mp4").Where(f => !f.Contains(".faststart.", StringComparison.OrdinalIgnoreCase)).ToArray();

    [SkippableFact]
    public async Task ACaptureThatStopsDeliveringFrames_IsReopened_ByTheFrameWatch()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var ffmpeg = Path.Combine(TestAssets.FfmpegDir!, "ffmpeg.exe");
        var dir = Path.Combine(Path.GetTempPath(), $"baioss-stall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var nut = Path.Combine(dir, "burst.nut");
        // 3 s de vídeo en crudo (uyvy422, lo que entrega una DeckLink): lo que la «tarjeta» entrega antes de quedarse muda.
        var gen = Process.Start(new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, ArgumentList =
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25,format=uyvy422",
            "-t", "3", "-c:v", "rawvideo", "-f", "nut", "-y", nut,
        } })!;
        await gen.WaitForExitAsync();
        Assert.Equal(0, gen.ExitCode);
        var burst = await File.ReadAllBytesAsync(nut);

        // La «tarjeta»: un servidor TCP que entrega la ráfaga y luego se CALLA sin cerrar (el receptor la lee como entrada).
        var card = new TcpListener(IPAddress.Loopback, 0);
        card.Start();
        int port = ((IPEndPoint)card.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var held = new List<TcpClient>();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await card.AcceptTcpClientAsync(stop.Token);
                    lock (held) held.Add(client);
                    try { await client.GetStream().WriteAsync(burst, stop.Token); } catch { /* el receptor murió */ }
                }
            }
            catch (OperationCanceledException) { }
        });

        int lost = 0;
        await using var receiver = new RawCaptureReceiver("muda", () => new[] { "-f", "nut", "-i", $"tcp://127.0.0.1:{port}" },
            copyStreams: true, ffmpeg, NullLogger.Instance)
        {
            FrameStallTimeout = TimeSpan.FromSeconds(3), FrameWatchInterval = TimeSpan.FromMilliseconds(200),
        };
        receiver.Lost += (_, _) => Interlocked.Increment(ref lost);
        try
        {
            await receiver.StartAsync();
            await WaitUntilAsync(() => receiver.VideoFramesForwarded >= 50, TimeSpan.FromSeconds(15), "la ráfaga cruzó el relé");
            // Ahora la tarjeta calla. FFmpeg sigue vivo e imprimiendo progreso: solo la vigilancia de frames lo ve.
            await WaitUntilAsync(() => receiver.Restarts >= 1, TimeSpan.FromSeconds(15), "reapertura por falta de frames");
            Assert.True(Volatile.Read(ref lost) >= 1, "la fuente no se enteró de la reapertura");
        }
        finally
        {
            stop.Cancel();
            card.Stop();
            lock (held) foreach (var c in held) c.Dispose();
            try { await serving; } catch { /* cierre */ }
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
