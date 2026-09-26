using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Con una entrada de red, Grabar y Detener NO interrumpen el preview: el proceso nuevo arranca antes de retirar el viejo
/// (el relé reparte el flujo a los dos), se salta en su preview el pre-roll que sí graba, y toma el mando cuando ya va a
/// cadencia real. Se mide con el motor real y un emisor RTMP local LIGERO (720p, x264 ultrafast): uno pesado en la misma
/// máquina se queda sin CPU mientras el proceso nuevo digiere el pre-roll y deja de generar frames (eso es el banco, no el
/// motor). Antes: 3 s de preview congelado en cada transición y, luego, el pre-roll reproducido a ×3–×4.
/// </summary>
public sealed class NetworkPreviewContinuityTests
{
    private static RecordingProfile Profile() => new()
    {
        Name = "net", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(2), GopSize = 30,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    /// <summary>Emisor RTMP local (servidor <c>-listen 1</c>); <paramref name="progress"/> recibe el instante de cada bloque de progreso
    /// (uno por segundo): dice si el emisor sigue produciendo o se quedó bloqueado.</summary>
    internal static Process LightSender(int port, Action<string>? progress = null)
    {
        var psi = new ProcessStartInfo(Path.Combine(TestAssets.FfmpegDir!, "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-stats_period", "0.5", "-re", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                                  "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "2M", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k",
                                  "-f", "flv", "-listen", "1", $"rtmp://127.0.0.1:{port}/live/test" })
            psi.ArgumentList.Add(a);
        var sender = Process.Start(psi)!;
        // En hilos propios (no BeginOutputReadLine): el banco no debe ocupar hilos del pool que el motor necesita.
        ProcessOutput.ReadLines(sender.StandardOutput, l => { if (l.StartsWith("frame=", StringComparison.Ordinal)) progress?.Invoke(l); }, "sender-progress");
        ProcessOutput.ReadLines(sender.StandardError, l => progress?.Invoke("stderr: " + l), "sender-log");
        return sender;
    }

    private static async Task<double> DurationAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string s = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    /// <summary>Con NET_CONTINUITY_LOG: recoge el registro del motor, el receptor y el relé para diagnosticar un fallo.</summary>
    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger, Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly Action<string> _sink;
        private readonly string _name;
        public CaptureLogger(Action<string> sink, string name = "") { _sink = sink; _name = name; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level >= Microsoft.Extensions.Logging.LogLevel.Debug;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            string msg = formatter(state, ex);
            if (msg.StartsWith("Pipeline canal", StringComparison.Ordinal)) msg = msg[..Math.Min(msg.Length, 120)] + "…";
            _sink($"[{level}]{_name} {msg}{(ex is null ? "" : " · " + ex.GetType().Name + ": " + ex.Message)}");
        }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CaptureLogger(_sink, " " + categoryName.Split('.').Last());
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }
    }

    [SkippableFact]
    public async Task RecordStartAndStop_NeitherFreezeNorFastForward_ThePreview_AndTheRecordingKeepsThePreroll()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-net-continuity-{Guid.NewGuid():N}");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        const int port = 19352;
        var sw = Stopwatch.StartNew();
        var senderProgress = new List<(double At, string Line)>();
        using var sender = LightSender(port, l => { lock (senderProgress) senderProgress.Add((sw.Elapsed.TotalSeconds, l)); });
        var frames = new List<double>();
        var flow = new List<(double At, long Received, long Forwarded, string Stage)>(); // el relé, muestreado: ¿sigue llegando flujo? ¿se reparte? ¿en qué está?
        using var sampling = new CancellationTokenSource();
        string? logPath = Environment.GetEnvironmentVariable("NET_CONTINUITY_LOG");
        var logLines = new List<string>();
        void Log(string s) { lock (logLines) logLines.Add($"{sw.Elapsed.TotalSeconds,7:0.00} s {s}"); }
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory = logPath is null ? NullLoggerFactory.Instance : new CaptureLogger(Log);
        long lastCompleted = 0;
        try
        {
            await Task.Delay(1500);
            var factory = new NetworkStreamCaptureSourceFactory(locator, loggerFactory);
            await using var source = (NetworkStreamCaptureSource)factory.Create(new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = "127.0.0.1", Port = port, Path = "live/test" }
                .ToInputSource(Guid.NewGuid(), "rtmp local ligero"));
            await source.OpenAsync();
            await using var engine = new FfmpegChannelEngine(locator, loggerFactory.CreateLogger("Engine")) { OutputRoot = outputRoot };
            engine.FrameReady += (_, _) => { lock (frames) frames.Add(sw.Elapsed.TotalSeconds); };
            // El muestreador corre en un hilo PROPIO (no del pool): si el pool se agota, el muestreo lo cuenta en vez de sufrirlo.
            var sampler = new Thread(() =>
            {
                var me = Process.GetCurrentProcess();
                var lastCpu = new Dictionary<int, TimeSpan>();
                while (!sampling.IsCancellationRequested)
                {
                    // Hilos del proceso: cuántos corren (CPU) y cuántos esperan; y qué hilos consumieron CPU en los últimos 100 ms.
                    me.Refresh();
                    int running = 0, waiting = 0; var hot = new List<string>();
                    foreach (ProcessThread t in me.Threads)
                    {
                        try
                        {
                            if (t.ThreadState == System.Diagnostics.ThreadState.Running) running++; else if (t.ThreadState == System.Diagnostics.ThreadState.Wait) waiting++;
                            var cpu = t.TotalProcessorTime;
                            if (lastCpu.TryGetValue(t.Id, out var prev) && (cpu - prev).TotalMilliseconds >= 50) hot.Add($"{t.Id}:{(cpu - prev).TotalMilliseconds:0}ms");
                            lastCpu[t.Id] = cpu;
                        }
                        catch { /* el hilo pudo terminar */ }
                    }
                    // El pool de hilos: pendientes que no avanzan y ~0 completados por muestra = pool agotado (hilos bloqueados),
                    // que fue la causa de 13 s de preview congelado por Grabar (ver ProcessOutput).
                    long completed = ThreadPool.CompletedWorkItemCount;
                    long delta = completed - Interlocked.Exchange(ref lastCompleted, completed);
                    string stage = $"{source.RelayPumpStage} · pool {ThreadPool.ThreadCount} hilos, {ThreadPool.PendingWorkItemCount} pendientes, {delta} completados · hilos SO: {running} corriendo/{waiting} esperando · calientes: {string.Join(" ", hot)}";
                    lock (flow) flow.Add((sw.Elapsed.TotalSeconds, source.RelaySourceBytes, source.RelayForwardedBytes, stage));
                    Thread.Sleep(100);
                }
            }) { IsBackground = true };
            sampler.Start();
            await engine.StartPreviewAsync(source, Profile(), "NET");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (source.CurrentSignal.State != SignalState.Locked && DateTime.UtcNow < deadline) await Task.Delay(250);
            Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
            await Task.Delay(6000); // preview en régimen (el primer proceso analiza 2 s en vivo al conectar: el pre-roll aún es corto)

            double tStart = sw.Elapsed.TotalSeconds;
            await engine.StartRecordingAsync(Guid.NewGuid(), Profile());
            await Task.Delay(12000);
            double tStop = sw.Elapsed.TotalSeconds;
            await engine.StopRecordingAsync();
            await Task.Delay(6000);
            sampling.Cancel();

            double[] f; lock (frames) f = frames.ToArray();
            (double At, long Received, long Forwarded, string Stage)[] fl; lock (flow) fl = flow.ToArray();
            (double At, string Line)[] sp; lock (senderProgress) sp = senderProgress.ToArray();
            // Bytes repartidos por el relé entre dos instantes: si el emisor (o el receptor) se paró, el hueco no es del motor.
            long FlowBetween(double a, double b) { var w = fl.Where(s => s.At >= a && s.At <= b).ToArray(); return w.Length < 2 ? 0 : w[^1].Forwarded - w[0].Forwarded; }
            long ReceivedBetween(double a, double b) { var w = fl.Where(s => s.At >= a && s.At <= b).ToArray(); return w.Length < 2 ? 0 : w[^1].Received - w[0].Received; }
            if (logPath is not null)
            {
                var lines = new List<string> { $"Grabar @{tStart:0.00} s · Detener @{tStop:0.00} s" };
                for (double t = tStart - 3; t < tStop + 6; t += 0.5)
                {
                    var stages = fl.LastOrDefault(s => s.At >= t && s.At < t + 0.5).Stage ?? "(sin muestras)";
                    var senderLine = sp.LastOrDefault(p => p.At >= t && p.At < t + 0.5).Line;
                    lines.Add($"  {t,6:0.0} s: {f.Count(x => x >= t && x < t + 0.5),3} frames · relé recibe {ReceivedBetween(t, t + 0.5) / 1024,6} KB · reparte {FlowBetween(t, t + 0.5) / 1024,6} KB · {stages} · emisor {senderLine ?? "(sin progreso)"}");
                }
                lock (logLines) lines.AddRange(logLines);
                lines.AddRange(sp.Where(p => p.Line.StartsWith("stderr")).Select(p => $"{p.At,7:0.00} s emisor {p.Line}"));
                File.AppendAllLines(logPath, lines);
            }
            // 1) Sin congelación del MOTOR: ningún hueco > 0,5 s entre frames de preview, desde 2 s antes de Grabar hasta 5 s
            //    después de Detener, mientras el relé siguió repartiendo flujo (un emisor que se para no cuenta).
            var gaps = new List<string>();
            for (int i = 1; i < f.Length; i++)
                if (f[i] > tStart - 2 && f[i - 1] < tStop + 5 && f[i] - f[i - 1] > 0.5 && FlowBetween(f[i - 1], f[i]) > 64 * 1024)
                    gaps.Add($"{f[i] - f[i - 1]:0.00} s @{f[i - 1]:0.0} s con {FlowBetween(f[i - 1], f[i]) / 1024} KB de flujo");
            Assert.True(gaps.Count == 0, "El preview se congeló con flujo disponible: " + string.Join(", ", gaps));
            // 2) Sin avance rápido: en ningún cuarto de segundo de los 5 s posteriores a Grabar o a Detener llegan más de 2,5× los
            //    frames nominales (30 fps → 7,5 por cuarto; el pre-roll reproducido a ×3–×4 daba 25–30).
            foreach (var (t, what) in new[] { (tStart, "Grabar"), (tStop, "Detener") })
            {
                var windows = Enumerable.Range(0, 20).Select(i => f.Count(x => x >= t + i * 0.25 && x < t + (i + 1) * 0.25)).ToArray();
                Assert.True(windows.Max() <= 19, $"Avance rápido tras {what}: {string.Join(" ", windows)} frames por 250 ms");
            }
            // 3) La grabación sigue empezando ANTES del botón (pre-roll desde un fotograma clave, ≥ 2,5 s de flujo).
            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), "No se generó el archivo.");
            double duration = await DurationAsync(locator.FfprobePath, file!);
            Assert.True(duration >= 12 + 2.0, $"El archivo dura {duration:0.0} s para 12 s de botón: el pre-roll no se grabó.");
        }
        finally
        {
            sampling.Cancel();
            try { if (!sender.HasExited) sender.Kill(entireProcessTree: true); } catch { /* ya salió */ }
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }
}
