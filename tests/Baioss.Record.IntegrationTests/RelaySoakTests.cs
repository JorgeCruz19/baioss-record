using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Prueba de resistencia del relé en crudo con el motor real (BAIOSS_SOAK=1, ≈2–3 min): cuatro canales DeckLink
/// sintéticos, ciclos de Grabar/Detener en paralelo, una caída provocada del proceso de captura por canal, y al final
/// nada fugado: mismos procesos FFmpeg que al empezar (receptor + preview por canal), hilos, handles y memoria del
/// proceso acotados, todos los archivos grabados válidos, ningún error en el registro y Detener siempre rápido. Al
/// disponer todo, cero procesos FFmpeg propios vivos.
/// </summary>
public sealed class RelaySoakTests
{
    private sealed record Snapshot(int Ffmpeg, int Threads, int Handles, long PrivateBytes, long Managed)
    {
        public static Snapshot Take()
        {
            // Primero la basura: lo que se mide es lo RETENIDO (una fuga), no lo que espera al recolector o a un finalizador.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var me = Process.GetCurrentProcess();
            me.Refresh();
            return new Snapshot(Process.GetProcessesByName("ffmpeg").Length, me.Threads.Count, me.HandleCount, me.PrivateMemorySize64, GC.GetTotalMemory(forceFullCollection: false));
        }
        public override string ToString() => $"ffmpeg {Ffmpeg}, hilos {Threads}, handles {Handles}, privados {PrivateBytes / 1_000_000} MB, gestionados {Managed / 1_000_000} MB";
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
    public async Task FourRelayChannels_RecordStopCyclesAndCaptureDeaths_LeakNothing()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        Skip.IfNot(Environment.GetEnvironmentVariable("BAIOSS_SOAK") == "1", "Prueba de resistencia: BAIOSS_SOAK=1 (≈2–3 min).");
        int channels = 4;
        int cycles = int.TryParse(Environment.GetEnvironmentVariable("BAIOSS_SOAK_CYCLES"), out var c) ? c : 12;
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-soak-{Guid.NewGuid():N}");
        string? logPath = Environment.GetEnvironmentVariable("BAIOSS_SOAK_LOG");
        var sw = Stopwatch.StartNew();
        var problems = new ConcurrentBag<string>();  // Error/Critical del motor, el receptor o el relé
        var warnings = new ConcurrentBag<string>();
        var lines = new ConcurrentBag<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(s =>
        {
            lines.Add($"{sw.Elapsed.TotalSeconds,7:0.00} s {s}");
            if (s.StartsWith("[Error]", StringComparison.Ordinal) || s.StartsWith("[Critical]", StringComparison.Ordinal)) problems.Add(s);
            else if (s.StartsWith("[Warning]", StringComparison.Ordinal)) warnings.Add(s);
        });

        var before = Snapshot.Take();
        var sources = new List<DecklinkCaptureSource>();
        var receivers = new List<RawCaptureReceiver>();
        var engines = new List<FfmpegChannelEngine>();
        var frames = new int[channels];
        var stopLatencies = new ConcurrentBag<double>();
        try
        {
            for (int i = 0; i < channels; i++)
            {
                var (source, _) = DecklinkRelayContinuityTests.SyntheticSource(locator, DecklinkRelayContinuityTests.SyntheticDevice(640, 360, 25), loggers);
                sources.Add(source);
                await source.OpenAsync();
                receivers.Add((RawCaptureReceiver)source.Receiver!);
                var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger($"Engine{i}")) { OutputRoot = Path.Combine(outputRoot, $"ch{i}") };
                int idx = i;
                engine.FrameReady += (_, _) => Interlocked.Increment(ref frames[idx]);
                engines.Add(engine);
                await engine.StartPreviewAsync(source, DecklinkRelayContinuityTests.Profile(), $"S{i}");
            }
            for (int i = 0; i < channels; i++)
            {
                int idx = i;
                await WaitUntilAsync(() => sources[idx].CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), $"canal {idx} abra");
                await WaitUntilAsync(() => Volatile.Read(ref frames[idx]) >= 25, TimeSpan.FromSeconds(20), $"preview del canal {idx}");
            }
            await Task.Delay(2000);
            var start = Snapshot.Take();
            Assert.Equal(before.Ffmpeg + channels * 2, start.Ffmpeg); // receptor + preview por canal

            var tasks = Enumerable.Range(0, channels).Select(async i =>
            {
                var engine = engines[i]; var source = sources[i]; var receiver = receivers[i];
                for (int k = 0; k < cycles; k++)
                {
                    await engine.StartRecordingAsync(Guid.NewGuid(), DecklinkRelayContinuityTests.Profile());
                    await Task.Delay(4000);
                    var t = Stopwatch.StartNew();
                    await engine.StopRecordingAsync();
                    stopLatencies.Add(t.Elapsed.TotalSeconds);
                    await Task.Delay(1000);
                    if (k == cycles / 2)
                    {
                        // Caída provocada del proceso de captura EN REPOSO: la fuente pasa a sin señal, el receptor
                        // relanza, el relé cierra al preview (EOF) y el motor lo levanta solo.
                        int seen = Volatile.Read(ref frames[i]);
                        Assert.True(receiver.KillProcessForTest(), $"canal {i}: no había proceso de captura que matar");
                        await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.NoSignal, TimeSpan.FromSeconds(10), $"canal {i} pierda la señal");
                        await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(30), $"canal {i} recupere la señal");
                        await WaitUntilAsync(() => Volatile.Read(ref frames[i]) >= seen + 25, TimeSpan.FromSeconds(30), $"preview del canal {i} vuelva");
                        await Task.Delay(1000);
                    }
                }
            }).ToArray();
            await Task.WhenAll(tasks);
            await Task.Delay(5000); // verificaciones ffprobe y remux de los últimos archivos en segundo plano

            var end = Snapshot.Take();
            var files = Directory.GetFiles(outputRoot, "*.mp4", SearchOption.AllDirectories);
            var small = files.Where(f => new FileInfo(f).Length < 200_000).Select(Path.GetFileName).ToList();
            var latencies = stopLatencies.OrderBy(x => x).ToArray();
            double p95 = latencies[(int)Math.Floor(0.95 * (latencies.Length - 1))];
            string summary = $"antes {before} · en marcha {start} · final {end} · archivos {files.Length} · Detener p50 {latencies[latencies.Length / 2]:0.00} s, p95 {p95:0.00} s, máx {latencies[^1]:0.00} s · avisos {warnings.Count} · errores {problems.Count}";
            if (logPath is not null)
            {
                File.AppendAllLines(logPath, new[] { summary }.Concat(problems).Concat(warnings.Take(50)).Concat(lines.OrderBy(l => l).Where(l => l.Contains("relevo") || l.Contains("detenida") || l.Contains("reabre") || l.Contains("relanz"))));
            }

            Assert.True(problems.IsEmpty, "Errores en el registro: " + string.Join(" | ", problems.Take(5)));
            Assert.Equal(channels * cycles, files.Length);
            Assert.True(small.Count == 0, "Archivos demasiado pequeños: " + string.Join(", ", small));
            Assert.All(receivers, r => Assert.Equal(1, r.Restarts)); // solo la caída provocada
            Assert.All(engines, e => Assert.Equal(RecordingState.Idle, e.State));
            Assert.Equal(start.Ffmpeg, end.Ffmpeg); // ni un FFmpeg de más ni de menos
            Assert.True(end.Threads <= start.Threads + 12, $"hilos: {start.Threads} → {end.Threads}");
            Assert.True(end.Handles <= start.Handles + 150, $"handles: {start.Handles} → {end.Handles}");
            Assert.True(end.PrivateBytes <= start.PrivateBytes + 250_000_000, $"memoria privada: {start.PrivateBytes / 1_000_000} → {end.PrivateBytes / 1_000_000} MB");
            Assert.True(p95 <= 3.0 && latencies[^1] <= 6.0, $"Detener lento: p95 {p95:0.00} s, máx {latencies[^1]:0.00} s");
        }
        finally
        {
            foreach (var e in engines) { try { await e.DisposeAsync(); } catch { /* best effort */ } }
            foreach (var s in sources) { try { await s.DisposeAsync(); } catch { /* best effort */ } }
            await Task.Delay(2000);
            var after = Snapshot.Take();
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
            Assert.Equal(before.Ffmpeg, after.Ffmpeg); // al disponer, todos los FFmpeg propios han muerto
        }
    }
}
