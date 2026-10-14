using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Cola de escritura de la grabación con el FFmpeg REAL. (1) El FFmpeg empaquetado admite <c>-thread_queue_size</c> en las
/// salidas (si un FFmpeg no la admite, el cableado no la usa). (2) Con el argv de <see cref="FfmpegArgumentBuilder.BuildLive"/>,
/// una salida de grabación que deja de aceptar escrituras unos segundos —un disco que despierta de la suspensión, el caso de
/// producción del 2026-10-12— NO congela el preview del mismo proceso; sin la cola, sí (control: el banco reproduce el problema).
/// </summary>
public sealed class WriteQueueStallTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Width = 320, Height = 180, FrameBytes = Width * Height * 4;
    private const double StallFrom = 3.0, StallTo = 7.0, RunFor = 9.5;

    [SkippableFact]
    public async Task BundledFfmpeg_AcceptsTheQueueOnAnOutput()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        Assert.True(await new FfmpegLocator(TestAssets.FfmpegDir!).SupportsOutputThreadQueueAsync());
    }

    [SkippableFact]
    public async Task StalledRecordingOutput_DoesNotFreezeThePreview_WithTheQueue()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);

        double without = await MaxPreviewGapAsync(locator.FfmpegPath, TimeSpan.Zero);
        double with = await MaxPreviewGapAsync(locator.FfmpegPath, TimeSpan.FromSeconds(10));
        output.WriteLine($"Grabación sin escribir de {StallFrom} a {StallTo} s → hueco máx. del preview: sin cola {without:0.00} s, con cola {with:0.00} s.");

        // Control: sin cola, el bloqueo de la escritura para el proceso entero (si no, este banco no probaría nada)…
        Assert.True(without > 2.0, $"Sin cola el preview debería congelarse mientras la grabación no escribe: hueco máx. {without:0.00} s");
        // …y con ella el preview sigue a su ritmo mientras la grabación espera al «disco».
        Assert.True(with < 0.6, $"Con cola el preview no debe congelarse: hueco máx. {with:0.00} s (sin cola: {without:0.00} s)");
    }

    /// <summary>Lanza el argv de BuildLive (vídeo sintético a tiempo real, sin audio) con la grabación hacia un socket que
    /// deja de leerse de <see cref="StallFrom"/> a <see cref="StallTo"/> s, y devuelve el mayor hueco entre frames del preview
    /// en ese tramo.</summary>
    private static async Task<double> MaxPreviewGapAsync(string ffmpeg, TimeSpan queue)
    {
        var preview = new TcpListener(IPAddress.Loopback, 0);
        var record = new TcpListener(IPAddress.Loopback, 0);
        preview.Start();
        record.Start();
        try
        {
            var profile = new RecordingProfile
            {
                Name = "ts", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(8),
                GopSize = 50, AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Ts,
            };
            var builder = new FfmpegArgumentBuilder()
                .From(new SyntheticSource()).Using(profile).ForChannel("WQ").ToDirectory(Path.GetTempPath())
                .WithPreviewSink($"tcp://127.0.0.1:{((IPEndPoint)preview.LocalEndpoint).Port}")
                .WithSignalAnalysis(false)
                .WithWriteQueue(queue);
            var args = builder.BuildLive(recording: true, Width, Height).ToList();
            // La grabación va a un «disco» que controla el test: un socket con búferes pequeños que deja de leerse un rato.
            args[args.IndexOf(builder.OutputFilePath)] = $"tcp://127.0.0.1:{((IPEndPoint)record.LocalEndpoint).Port}?send_buffer_size=65536";

            var sw = Stopwatch.StartNew();
            var frames = new List<double>();
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            // -progress pipe:1 y el log: hay que leerlos o FFmpeg se bloquea al escribirlos.
            var readers = new[]
            {
                ProcessOutput.ReadLines(process.StandardOutput, _ => { }, "wq-progress"),
                ProcessOutput.ReadLines(process.StandardError, _ => { }, "wq-log"),
            };

            var previewLoop = Task.Run(async () =>
            {
                using var client = await preview.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[1 << 16];
                long pending = 0;
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    pending += n;
                    while (pending >= FrameBytes) { pending -= FrameBytes; lock (frames) frames.Add(sw.Elapsed.TotalSeconds); }
                }
            });
            var recordLoop = Task.Run(async () =>
            {
                using var client = await record.AcceptTcpClientAsync();
                client.ReceiveBufferSize = 64 * 1024;
                var stream = client.GetStream();
                var buffer = new byte[1 << 16];
                while (await stream.ReadAsync(buffer) > 0)
                {
                    double t = sw.Elapsed.TotalSeconds;
                    if (t >= StallFrom && t < StallTo) await Task.Delay(TimeSpan.FromSeconds(StallTo - t)); // no acepta nada
                }
            });

            await Task.Delay(TimeSpan.FromSeconds(RunFor));
            try { process.Kill(entireProcessTree: true); } catch { /* ya salió */ }
            await process.WaitForExitAsync();
            ProcessOutput.Join(TimeSpan.FromSeconds(2), readers);
            await Task.WhenAny(Task.WhenAll(previewLoop, recordLoop), Task.Delay(TimeSpan.FromSeconds(3)));

            double[] f;
            lock (frames) f = frames.ToArray();
            Assert.True(f.Count(x => x < StallFrom) > 30, $"El preview no arrancó a tiempo ({f.Length} frames en total).");
            double maxGap = 0;
            for (int i = 1; i < f.Length; i++)
                if (f[i] > StallFrom - 0.5 && f[i - 1] < StallTo + 1.0) maxGap = Math.Max(maxGap, f[i] - f[i - 1]);
            if (f.Length > 0 && f[^1] < StallTo + 1.0) maxGap = Math.Max(maxGap, RunFor - f[^1]); // se paró y no volvió
            return maxGap;
        }
        finally
        {
            preview.Stop();
            record.Stop();
        }
    }

    /// <summary>Vídeo sintético a tiempo real (como una señal en directo), sin audio.</summary>
    private sealed class SyntheticSource : ICaptureSource
    {
        public InputSource Definition { get; } = new() { Name = "sintética", Type = InputType.File, Uri = "lavfi" };
        public SignalInfo CurrentSignal { get; } = new(SignalState.Locked, new Resolution(1280, 720), new FrameRate(30, 1),
            null, HasAudio: false, Timecode: null, Bitrate: null);
        public event EventHandler<SignalInfo>? SignalChanged { add { } remove { } }
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<string> BuildInputArguments() => new[] { "-re", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30" };
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
