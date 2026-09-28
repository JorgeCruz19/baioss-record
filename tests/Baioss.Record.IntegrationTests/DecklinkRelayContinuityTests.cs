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
/// DeckLink en modo dispositivo persistente, con el motor REAL y una «tarjeta» sintética (lavfi en tiempo real, vídeo
/// uyvy422 + audio, servida por el mismo receptor y relé que usará la tarjeta de verdad): Grabar y Detener no congelan
/// el preview ni reabren el dispositivo, la grabación es válida, el relé aguanta 1080p y audio multicanal, y si el
/// proceso de captura muere el canal se recupera solo. Lo que no se puede probar aquí es el driver DeckLink: eso queda
/// para la máquina con la tarjeta.
/// </summary>
public sealed class DecklinkRelayContinuityTests
{
    private static RecordingProfile Profile() => new()
    {
        Name = "dl", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(2), GopSize = 30,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    /// <summary>Argumentos de «dispositivo» sintético: UNA entrada lavfi con vídeo en uyvy422 (lo que entrega una DeckLink) y
    /// audio, en tiempo real, como la tarjeta. El receptor los vuelca a rawvideo/pcm_s16le en NUT.</summary>
    internal static IReadOnlyList<string> SyntheticDevice(int width, int height, int fps, string audioLayout = "stereo") => new[]
    {
        "-re", "-f", "lavfi", "-i",
        string.Create(CultureInfo.InvariantCulture,
            $"testsrc2=size={width}x{height}:rate={fps},format=uyvy422[out0];sine=frequency=1000:sample_rate=48000,aformat=channel_layouts={audioLayout}[out1]"),
    };

    private static InputSource Definition(params (string Key, string Value)[] parameters)
    {
        var source = new InputSource { Name = "DeckLink sintética", Type = InputType.DecklinkSdi, Uri = "DeckLink sintética" };
        foreach (var (k, v) in parameters) source.Parameters[k] = v;
        return source;
    }

    private static (DecklinkCaptureSource Source, RawCaptureReceiver Receiver) SyntheticSource(FfmpegLocator locator, IReadOnlyList<string> device, Microsoft.Extensions.Logging.ILoggerFactory? loggers = null, params (string Key, string Value)[] parameters)
    {
        RawCaptureReceiver? receiver = null;
        var lf = loggers ?? NullLoggerFactory.Instance;
        var source = new DecklinkCaptureSource(Definition(parameters),
            _ => receiver = new RawCaptureReceiver("sintética", () => device, copyStreams: false, locator.FfmpegPath, lf.CreateLogger("Receiver")),
            lf.CreateLogger("Source"));
        return (source, receiver!);
    }

    /// <summary>Con DL_RELAY_LOG: recoge el registro del motor, el receptor y el relé para diagnosticar un fallo.</summary>
    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger, Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly Action<string> _sink;
        private readonly string _name;
        public CaptureLogger(Action<string> sink, string name = "") { _sink = sink; _name = name; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level >= Microsoft.Extensions.Logging.LogLevel.Debug;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => _sink($"[{level}]{_name} {formatter(state, ex)}{(ex is null ? "" : " · " + ex.GetType().Name + ": " + ex.Message)}");
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CaptureLogger(_sink, " " + categoryName.Split('.').Last());
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed record Probe(double Duration, int AudioChannels, string Raw);

    private static async Task<Probe> ProbeAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=start_time,duration:stream=codec_type,channels,start_time,duration,nb_frames,avg_frame_rate", "-of", "csv=p=0", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string s = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
        double duration = double.NaN; int channels = 0;
        foreach (var line in s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts[0] == "audio" && parts.Length > 1 && int.TryParse(parts[1], out int ch)) channels = ch;
            else if (parts[0] != "video" && parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) duration = d;
        }
        return new Probe(duration, channels, s.Trim());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Esperando: " + what);
            await Task.Delay(100);
        }
    }

    [SkippableFact]
    public async Task RecordStartAndStop_DoNotFreezeThePreview_AndNeverReopenTheDevice()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-dl-relay-{Guid.NewGuid():N}");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var sw = Stopwatch.StartNew();
        var frames = new List<double>();
        var flow = new List<(double At, long Forwarded)>();
        using var sampling = new CancellationTokenSource();
        try
        {
            var (source, _) = SyntheticSource(locator, SyntheticDevice(1280, 720, 25));
            await using var _s = source;
            await source.OpenAsync();
            var receiver = (RawCaptureReceiver)source.Receiver!;
            await using var engine = new FfmpegChannelEngine(locator, NullLogger<FfmpegChannelEngine>.Instance) { OutputRoot = outputRoot };
            engine.FrameReady += (_, _) => { lock (frames) frames.Add(sw.Elapsed.TotalSeconds); };
            var sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    lock (flow) flow.Add((sw.Elapsed.TotalSeconds, source.RelayForwardedBytes));
                    await Task.Delay(100);
                }
            });
            await engine.StartPreviewAsync(source, Profile(), "DL");
            await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "la tarjeta sintética abra");
            Assert.True(source.SupportsOverlappingProcesses);
            await WaitUntilAsync(() => { lock (frames) return frames.Count >= 25; }, TimeSpan.FromSeconds(20), "preview en marcha");
            await Task.Delay(3000); // preview en régimen

            double tStart = sw.Elapsed.TotalSeconds;
            await engine.StartRecordingAsync(Guid.NewGuid(), Profile());
            await Task.Delay(8000);
            double tStop = sw.Elapsed.TotalSeconds;
            await engine.StopRecordingAsync();
            await Task.Delay(4000);
            sampling.Cancel();

            double[] f; lock (frames) f = frames.ToArray();
            (double At, long Forwarded)[] fl; lock (flow) fl = flow.ToArray();
            long FlowBetween(double a, double b) { var w = fl.Where(s => s.At >= a && s.At <= b).ToArray(); return w.Length < 2 ? 0 : w[^1].Forwarded - w[0].Forwarded; }

            // 1) La tarjeta no se reabrió: el proceso de captura no se relanzó ni una vez en todo el ciclo.
            Assert.Equal(0, receiver.Restarts);
            // 2) Sin congelación: ningún hueco > 0,5 s entre frames de preview desde 2 s antes de Grabar hasta 3 s después de
            //    Detener, mientras el relé siguió repartiendo (un banco sin CPU no cuenta como fallo del motor).
            var gaps = new List<string>();
            for (int i = 1; i < f.Length; i++)
                if (f[i] > tStart - 2 && f[i - 1] < tStop + 3 && f[i] - f[i - 1] > 0.5 && FlowBetween(f[i - 1], f[i]) > 1024 * 1024)
                    gaps.Add($"{f[i] - f[i - 1]:0.00} s @{f[i - 1]:0.0} s");
            Assert.True(gaps.Count == 0, "El preview se congeló con flujo disponible: " + string.Join(", ", gaps));
            // 3) Sin avance rápido tras Grabar ni tras Detener: nunca más de 2,5× los frames nominales por cuarto de segundo.
            foreach (var (t, what) in new[] { (tStart, "Grabar"), (tStop, "Detener") })
            {
                var windows = Enumerable.Range(0, 16).Select(i => f.Count(x => x >= t + i * 0.25 && x < t + (i + 1) * 0.25)).ToArray();
                Assert.True(windows.Max() <= 16, $"Avance rápido tras {what}: {string.Join(" ", windows)} frames por 250 ms");
            }
            // 4) La grabación es válida y cubre lo pedido (sin pre-roll: el flujo en crudo no lo necesita).
            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), "No se generó el archivo.");
            var probe = await ProbeAsync(locator.FfprobePath, file!);
            Assert.True(probe.Duration is >= 8 - 1.0 and <= 8 + 2.0, $"Duración {probe.Duration:0.0} s para 8 s de botón. ffprobe: {probe.Raw}");
            Assert.Equal(2, probe.AudioChannels);
        }
        finally
        {
            sampling.Cancel();
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task CaptureProcessDeath_ReopensTheDevice_AndThePreviewRecoversAlone()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var sw = Stopwatch.StartNew();
        var frames = new List<double>();
        var states = new List<(double At, SignalState State, string? Label)>();
        var (source, _) = SyntheticSource(locator, SyntheticDevice(640, 360, 25));
        await using var _s = source;
        source.SignalChanged += (_, s) => { lock (states) states.Add((sw.Elapsed.TotalSeconds, s.State, s.FormatLabel)); };
        await source.OpenAsync();
        var receiver = (RawCaptureReceiver)source.Receiver!;
        await using var engine = new FfmpegChannelEngine(locator, NullLogger<FfmpegChannelEngine>.Instance) { OutputRoot = Path.GetTempPath() };
        engine.FrameReady += (_, _) => { lock (frames) frames.Add(sw.Elapsed.TotalSeconds); };
        await engine.StartPreviewAsync(source, Profile(), "DL");
        await WaitUntilAsync(() => { lock (frames) return frames.Count >= 25; }, TimeSpan.FromSeconds(20), "preview en marcha");

        double tKill = sw.Elapsed.TotalSeconds;
        Assert.True(receiver.KillProcessForTest(), "no había proceso de captura que matar");
        // La fuente pasa a SIN SEÑAL (reabriendo) y vuelve a SEÑAL OK cuando el receptor relanza la captura.
        await WaitUntilAsync(() => { lock (states) return states.Any(s => s.At >= tKill && s.State == SignalState.NoSignal); }, TimeSpan.FromSeconds(10), "sin señal tras la caída");
        await WaitUntilAsync(() => { lock (states) return states.Any(s => s.At >= tKill && s.State == SignalState.Locked); }, TimeSpan.FromSeconds(20), "señal recuperada");
        Assert.Equal(1, receiver.Restarts);
        // …y el preview vuelve solo: frames nuevos después de la recuperación.
        double tBack = sw.Elapsed.TotalSeconds;
        await WaitUntilAsync(() => { lock (frames) return frames.Count(x => x >= tBack) >= 25; }, TimeSpan.FromSeconds(20), "preview recuperado");
    }

    [SkippableFact]
    public async Task EightChannelAudioAt1080p_TravelsThroughTheRelay_AndTheRecordingKeepsTheChosenPair()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-dl-relay8-{Guid.NewGuid():N}");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        string? logPath = Environment.GetEnvironmentVariable("DL_RELAY_LOG");
        var sw = Stopwatch.StartNew();
        var logLines = new List<string>();
        Microsoft.Extensions.Logging.ILoggerFactory loggers = logPath is null ? NullLoggerFactory.Instance
            : new CaptureLogger(s => { lock (logLines) logLines.Add($"{sw.Elapsed.TotalSeconds,7:0.00} s {s}"); });
        try
        {
            var (source, _) = SyntheticSource(locator, SyntheticDevice(1920, 1080, 25, "7.1"), loggers, (AudioSelection.ChannelsKey, "8"), (AudioSelection.PairsKey, "2"));
            await using var _s = source;
            await source.OpenAsync();
            var receiver = (RawCaptureReceiver)source.Receiver!;
            await using var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine")) { OutputRoot = outputRoot };
            int frames = 0;
            engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);
            await engine.StartPreviewAsync(source, Profile(), "DL");
            await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "la tarjeta sintética abra");
            Assert.Equal(8, source.CurrentSignal.AudioChannels);
            await WaitUntilAsync(() => Volatile.Read(ref frames) >= 25, TimeSpan.FromSeconds(20), "preview en marcha");
            double tStart = sw.Elapsed.TotalSeconds;
            await engine.StartRecordingAsync(Guid.NewGuid(), Profile());
            await Task.Delay(5000);
            double tStop = sw.Elapsed.TotalSeconds;
            await engine.StopRecordingAsync();
            double tStopped = sw.Elapsed.TotalSeconds;
            await Task.Delay(1500);
            // 1080p en crudo (104 MB/s) más audio de 8 canales: el relé lo reparte sin relanzar la captura, y el archivo
            // lleva el par elegido (estéreo) a partir de las 8 pistas que viajaron por NUT.
            Assert.Equal(0, receiver.Restarts);
            Assert.True(receiver.VideoFramesForwarded > 100, $"el relé repartió {receiver.VideoFramesForwarded} frames");
            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), "No se generó el archivo.");
            var probe = await ProbeAsync(locator.FfprobePath, file!);
            if (logPath is not null)
            {
                // CPU del proceso de test (motor + relé + lectura del preview) por segundo de pared: el coste del relé en crudo.
                var me = Process.GetCurrentProcess();
                double cores = me.TotalProcessorTime.TotalSeconds / sw.Elapsed.TotalSeconds;
                lock (logLines) logLines.Add($"Grabar @{tStart:0.00} s · Detener @{tStop:0.00} s (volvió @{tStopped:0.00} s) · frames de preview {frames} · relé {receiver.VideoFramesForwarded} frames, {receiver.SourceBytes / 1_000_000} MB · proceso de la app {cores:0.00} núcleos de media · archivo {file}\nffprobe: {probe.Raw}");
                lock (logLines) File.AppendAllLines(logPath, logLines);
            }
            // El archivo empieza al conectar el proceso (medio segundo tras el botón) y termina cuando el preview nuevo pinta su
            // primer frame (sin pre-roll no hay que esperar a que vaya al día): Detener devuelve el control en ~1 s.
            Assert.True(probe.Duration is >= 4.5 and <= 8.5, $"Duración {probe.Duration:0.0} s para 5 s de botón. ffprobe: {probe.Raw}");
            Assert.Equal(2, probe.AudioChannels);
            Assert.True(tStopped - tStop < 3.0, $"Detener tardó {tStopped - tStop:0.0} s en devolver el control (relevo + cierre).");
        }
        finally
        {
            if (logPath is null) try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Con BAIOSS_RELAY_PERF=1: cuánto cuesta el relé en crudo. Empuja ~1 GB de NUT real de 1080p (el flujo de un
    /// segundo, repetido) por el relé a un consumidor que lo lee y descarta, y mide la CPU del proceso frente a lo que ese
    /// volumen tardaría en llegar de una tarjeta a 25 fps. Escribe el resultado en BAIOSS_RELAY_PERF_LOG si está definido.</summary>
    [SkippableFact]
    public async Task RelayThroughput_MeasuresCpuPerChannel()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        Skip.IfNot(Environment.GetEnvironmentVariable("BAIOSS_RELAY_PERF") == "1", "Medición de rendimiento: BAIOSS_RELAY_PERF=1.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        string file = Path.Combine(Path.GetTempPath(), $"baioss-nut-perf-{Guid.NewGuid():N}.nut");
        try
        {
            var psi = new ProcessStartInfo(locator.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=25,format=uyvy422[out0];sine=frequency=1000:sample_rate=48000,aformat=channel_layouts=stereo[out1]",
                                      "-t", "1", "-map", "0:v:0", "-map", "0:a:0", "-c:v", "rawvideo", "-c:a", "pcm_s16le", "-f", "nut", "-y", file })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode); }
            var bytes = await File.ReadAllBytesAsync(file);
            // Cabeceras + cuerpo (sin el índice final) repetido: un flujo NUT válido y largo.
            var reader = new NutStreamReader(new MemoryStream(bytes), pooled: false);
            int headerEnd = 0, indexStart = bytes.Length;
            int offset = 0;
            while (await reader.ReadUnitAsync() is { } u)
            {
                if (u.Kind == NutUnitKind.Syncpoint && headerEnd == 0) headerEnd = offset;
                if (u.Kind == NutUnitKind.Index) indexStart = offset;
                offset += u.Length;
            }
            int repeats = Math.Max(1, (int)(1_000_000_000L / (indexStart - headerEnd)));

            await using var relay = new RawStreamRelay("perf", NullLogger.Instance);
            relay.Start();
            using var consumer = new System.Net.Sockets.TcpClient();
            await consumer.ConnectAsync(System.Net.IPAddress.Loopback, relay.ConsumerPort);
            var sink = Task.Run(async () =>
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                var stream = consumer.GetStream();
                while (true) { int n = await stream.ReadAsync(buffer); if (n <= 0) return total; total += n; }
            });
            await Task.Delay(200);
            var me = Process.GetCurrentProcess();
            var cpu0 = me.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            using (var source = new System.Net.Sockets.TcpClient())
            {
                await source.ConnectAsync(System.Net.IPAddress.Loopback, relay.SourcePort);
                var stream = source.GetStream();
                await stream.WriteAsync(bytes.AsMemory(0, headerEnd));
                for (int i = 0; i < repeats; i++)
                {
                    await stream.WriteAsync(bytes.AsMemory(headerEnd, indexStart - headerEnd));
                    // A DOBLE velocidad de tarjeta (cada repetición es 1 s de flujo): mide el coste sin que el consumidor,
                    // que lee y descarta, se quede atrás y el relé le descarte frames (como haría con un origen sin freno).
                    var due = TimeSpan.FromSeconds((i + 1) * 0.5) - sw.Elapsed;
                    if (due > TimeSpan.Zero) await Task.Delay(due);
                }
            }
            long received = await sink;
            sw.Stop();
            me.Refresh();
            double cpuSeconds = (me.TotalProcessorTime - cpu0).TotalSeconds;
            long pushed = headerEnd + (long)repeats * (indexStart - headerEnd);
            double realtimeSeconds = repeats * 1.0; // cada repetición es 1 s de flujo de tarjeta
            string result = $"relé crudo 1080p25: {pushed / 1_000_000} MB en {sw.Elapsed.TotalSeconds:0.00} s ({pushed / 1_000_000 / sw.Elapsed.TotalSeconds:0} MB/s), " +
                            $"CPU del proceso {cpuSeconds:0.00} s = {cpuSeconds / realtimeSeconds:0.000} núcleos por canal a tiempo real (origen y consumidor incluidos)";
            Assert.Equal(pushed, received);
            string? log = Environment.GetEnvironmentVariable("BAIOSS_RELAY_PERF_LOG");
            if (log is not null) File.AppendAllText(log, result + Environment.NewLine);
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task RealNutFromFfmpeg_1080pWithChecksums_IsSplitLikeFfprobeCounts()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        string file = Path.Combine(Path.GetTempPath(), $"baioss-nut-{Guid.NewGuid():N}.nut");
        try
        {
            var psi = new ProcessStartInfo(locator.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=25,format=uyvy422[out0];sine=frequency=1000:sample_rate=48000,aformat=channel_layouts=7.1[out1]",
                                      "-t", "1", "-map", "0:v:0", "-map", "0:a:0", "-c:v", "rawvideo", "-c:a", "pcm_s16le", "-f", "nut", "-y", file })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode); }

            var probe = new ProcessStartInfo(locator.FfprobePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-show_entries", "packet=codec_type,size", "-of", "csv=p=0", file }) probe.ArgumentList.Add(a);
            using var pp = Process.Start(probe)!;
            var lines = (await pp.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await pp.WaitForExitAsync();
            int videoPackets = lines.Count(l => l.StartsWith("video", StringComparison.Ordinal));
            int audioPackets = lines.Count(l => l.StartsWith("audio", StringComparison.Ordinal));
            long videoBytes = lines.Where(l => l.StartsWith("video", StringComparison.Ordinal)).Sum(l => long.Parse(l.Split(',')[1], CultureInfo.InvariantCulture));

            await using var stream = File.OpenRead(file);
            var reader = new NutStreamReader(stream, pooled: true);
            int video = 0, audio = 0; long total = 0, videoUnitBytes = 0;
            while (await reader.ReadUnitAsync() is { } unit)
            {
                total += unit.Length;
                if (unit.IsVideoFrame) { video++; videoUnitBytes += unit.Length; }
                if (unit.IsAudioFrame) audio++;
                unit.Release();
            }
            Assert.Equal(25, videoPackets);
            Assert.Equal(videoPackets, video);
            Assert.Equal(audioPackets, audio);
            Assert.Equal(new FileInfo(file).Length, total);
            // Cada frame de 1080p (4.147.200 bytes) lleva cabecera con checksum: la unidad pesa unos bytes más que los datos.
            Assert.InRange(videoUnitBytes - videoBytes, video * 4, video * 24);
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}
