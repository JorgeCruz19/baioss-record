using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Presets;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Con BAIOSS_STOP_TAIL=1: cuánto graba de más y cuánto tarda cada Detener, con el motor real y una entrada DeckLink por el relé
/// (sintética, o la tarjeta con BAIOSS_STOP_TAIL_DEVICE). En cada ciclo graba unos segundos (con una espera al azar de hasta 1 s
/// más, para no parar siempre en la misma fase del bucle de FFmpeg) y mide:
/// <list type="bullet">
///   <item>el Detener (lo que espera el operador) y, del registro del motor, el relevo del preview y el cierre del archivo;</item>
///   <item>lo grabado tras pulsar Detener y tras empezar a cerrar el proceso, por los frames del flujo que llegaron a su archivo;</item>
///   <item>los frames que el relé le escribió al proceso que grababa y que no están en el archivo (perdidos al final);</item>
///   <item>el final del audio frente al del vídeo.</item>
/// </list>
/// Variables: BAIOSS_STOP_TAIL_CYCLES (8), BAIOSS_STOP_TAIL_SECONDS (5), BAIOSS_STOP_TAIL_PRESET («H.264 Broadcast · 1080i59.94 MP4»),
/// BAIOSS_STOP_TAIL_LABEL (rótulo de la pasada) y BAIOSS_STOP_TAIL_LOG (archivo al que se añade el resultado).
/// </summary>
public sealed class StopTailMeasurementTests
{
    private readonly ITestOutputHelper _out;
    public StopTailMeasurementTests(ITestOutputHelper output) => _out = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static readonly Regex Handover = new(@"relevo del preview en ([\d.,]+) s y archivo cerrado en ([\d.,]+) s(?: \((.+?)\))?", RegexOptions.Compiled);

    /// <param name="Read">Frames de vídeo que leyó el FFmpeg que grababa (su informe FFREPORT); −1 sin informe.</param>
    /// <param name="EndLoss">Frames que el relé le escribió y que no leyó, sin contar el del principio (ver el resumen); −1 sin informe.</param>
    private sealed record Row(int Cycle, double Stop, double Handover, double Close, string How, long Written, long Read, long InFile,
        long Unwritten, double PastStop, double PastRetire, long EndLoss, double AudioMinusVideo, double Video);

    [SkippableFact]
    public async Task MeasureStopTail()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        Skip.IfNot(Env("BAIOSS_STOP_TAIL") == "1", "Medición: BAIOSS_STOP_TAIL=1.");
        int cycles = int.Parse(Env("BAIOSS_STOP_TAIL_CYCLES") ?? "8", CultureInfo.InvariantCulture);
        double seconds = double.Parse(Env("BAIOSS_STOP_TAIL_SECONDS") ?? "5", CultureInfo.InvariantCulture);
        string presetName = Env("BAIOSS_STOP_TAIL_PRESET") ?? "H.264 Broadcast · 1080i59.94 MP4";
        string? device = Env("BAIOSS_STOP_TAIL_DEVICE");
        string label = Env("BAIOSS_STOP_TAIL_LABEL") ?? "";
        var profile = PresetCatalog.CreateBuiltIns().Single(p => p.Name == presetName).ToProfile();

        var clock = Stopwatch.StartNew();
        var lines = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(s => { lock (lines) lines.Add($"{clock.Elapsed.TotalSeconds,8:0.000} s {s}"); });
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        string dir = Path.Combine(Path.GetTempPath(), $"baioss-stoptail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        // Con BAIOSS_STOP_TAIL_FFREPORT=1, cada FFmpeg deja su informe (lo que leyó de cada entrada) en una carpeta aparte.
        string? reports = Env("BAIOSS_STOP_TAIL_FFREPORT") == "1" ? Path.Combine(Path.GetTempPath(), $"baioss-stoptail-reports-{Guid.NewGuid():N}") : null;
        string? previousReport = Environment.GetEnvironmentVariable("FFREPORT");
        if (reports is not null)
        {
            Directory.CreateDirectory(reports);
            Environment.SetEnvironmentVariable("FFREPORT", $"file='{reports.Replace('\\', '/')}/%p-%t.log':level=40");
        }
        var rows = new List<Row>();
        try
        {
            DecklinkCaptureSource source;
            if (device is null)
            {
                // Lo que entrega una DeckLink con señal 1080i59.94: uyvy422 a 29,97 y estéreo, en tiempo real.
                var synthetic = new[]
                {
                    "-re", "-f", "lavfi", "-i",
                    "testsrc2=size=1920x1080:rate=30000/1001,format=uyvy422[out0];sine=frequency=1000:sample_rate=48000,aformat=channel_layouts=stereo[out1]",
                };
                (source, _) = DecklinkRelayContinuityTests.SyntheticSource(locator, synthetic, loggers);
            }
            else
            {
                var definition = new InputSource { Name = $"DeckLink — {device}", Type = InputType.DecklinkSdi, Uri = device };
                source = (DecklinkCaptureSource)new DecklinkCaptureSourceFactory(locator, loggers, relay: true).Create(definition);
            }
            await using var _s = source;
            await source.OpenAsync();
            var receiver = (RawCaptureReceiver)source.Receiver!;
            var closed = new List<(double At, RawStreamRelay.ConsumerReport Report)>();
            receiver.Relay.ConsumerClosed += r => { lock (closed) closed.Add((clock.Elapsed.TotalSeconds, r)); };
            // Frames que el relé lleva repartidos, cada 10 ms: para saber qué frame del flujo pasaba en cada instante.
            var flow = new List<(double At, long Frames)>();
            using var sampling = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    lock (flow) flow.Add((clock.Elapsed.TotalSeconds, receiver.VideoFramesForwarded));
                    try { await Task.Delay(10, sampling.Token); } catch (OperationCanceledException) { return; }
                }
            });
            long FrameAt(double t) { lock (flow) return flow.LastOrDefault(f => f.At <= t).Frames; }

            var queue = await locator.SupportsOutputThreadQueueAsync() ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
            await using var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine")) { OutputRoot = dir, WriteQueue = queue, FragmentedMp4 = false };
            int previewFrames = 0;
            engine.FrameReady += (_, _) => Interlocked.Increment(ref previewFrames);
            await engine.StartPreviewAsync(source, profile, "A");
            await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
            await WaitUntilAsync(() => Volatile.Read(ref previewFrames) > 30, TimeSpan.FromSeconds(20), "preview en marcha");
            double fps = source.CurrentSignal.FrameRate?.Value ?? 29.97;
            await Task.Delay(2000);

            var random = new Random(7);
            for (int i = 1; i <= cycles; i++)
            {
                await engine.StartRecordingAsync(Guid.NewGuid(), profile);
                await Task.Delay(TimeSpan.FromSeconds(seconds + random.NextDouble()));
                double tStop = clock.Elapsed.TotalSeconds;
                long atStop = receiver.VideoFramesForwarded;
                await engine.StopRecordingAsync();
                double tStopped = clock.Elapsed.TotalSeconds;
                await Task.Delay(1500);

                double handover = double.NaN, close = double.NaN;
                string how = "?";
                lock (lines)
                    foreach (var l in lines)
                        if (Handover.Match(l) is { Success: true } m && double.TryParse(l.AsSpan(0, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var at) && at >= tStop && at <= tStopped + 0.1)
                        {
                            handover = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                            close = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                            how = m.Groups[3].Success ? m.Groups[3].Value : "«q»";
                        }
                RawStreamRelay.ConsumerReport recording;
                lock (closed) recording = closed.Where(r => r.At >= tStop && r.At <= tStopped + 1).OrderByDescending(r => r.Report.VideoFramesWritten).First().Report;
                var file = engine.LastOutputFile!;
                var (inFile, video, audio) = await ProbeAsync(locator.FfprobePath, file);
                long read = reports is null ? -1 : ReadVideoPackets(reports, file);
                // El FFmpeg que entra a mitad del flujo descarta el primer frame tras el punto de sincronía en que arranca (su
                // demuxer NUT rechaza los puntos de sincronía que apuntan antes del inicio): ese no es del final.
                long endLoss = read < 0 ? -1 : Math.Max(0, recording.VideoFramesWritten - read - 1);
                long lastKept = recording.LastVideoFrameWritten - Math.Max(0, endLoss);
                rows.Add(new Row(i, tStopped - tStop, handover, close, how, recording.VideoFramesWritten, read, inFile,
                    recording.VideoFramesQueued - recording.VideoFramesWritten, (lastKept - atStop) / fps, (lastKept - FrameAt(tStopped - close)) / fps,
                    endLoss, audio - video, video));
                File.Delete(file);
            }
            sampling.Cancel();
            await sampler;
        }
        finally
        {
            if (reports is not null)
            {
                Environment.SetEnvironmentVariable("FFREPORT", previousReport);
                try { Directory.Delete(reports, true); } catch { /* best effort */ }
            }
            string Stats(Func<Row, double> f, string format)
            {
                var v = rows.Select(f).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
                return v.Length == 0 ? "—" : $"{v[0].ToString(format, CultureInfo.InvariantCulture)} / {v[v.Length / 2].ToString(format, CultureInfo.InvariantCulture)} / {v[^1].ToString(format, CultureInfo.InvariantCulture)}";
            }
            var report = new List<string>
            {
                $"## {label} · {(device ?? "DeckLink sintética 1080 29,97")} · {presetName} · {rows.Count} ciclos · {DateTime.Now:yyyy-MM-dd HH:mm}",
                "ciclo · Detener s · relevo s · cierre s · cierre por · escritos al proceso · leídos por FFmpeg · en el archivo · sin escribir · grabado tras Detener s · tras empezar a cerrar s · perdidos al final · audio−vídeo s · vídeo s",
            };
            report.AddRange(rows.Select(r => string.Create(CultureInfo.InvariantCulture,
                $"{r.Cycle} · {r.Stop:0.000} · {r.Handover:0.00} · {r.Close:0.00} · {r.How} · {r.Written} · {(r.Read < 0 ? "—" : r.Read.ToString(CultureInfo.InvariantCulture))} · {r.InFile} · {r.Unwritten} · " +
                $"{r.PastStop:+0.000;-0.000} · {r.PastRetire:+0.000;-0.000} · {(r.EndLoss < 0 ? "—" : r.EndLoss.ToString(CultureInfo.InvariantCulture))} · {r.AudioMinusVideo:+0.000;-0.000} · {r.Video:0.000}")));
            report.Add($"mín / mediana / máx: Detener {Stats(r => r.Stop, "0.000")} s · cierre {Stats(r => r.Close, "0.00")} s · tras Detener {Stats(r => r.PastStop, "0.000")} s · " +
                       $"tras empezar a cerrar {Stats(r => r.PastRetire, "0.000")} s · perdidos al final {(reports is null ? "—" : Stats(r => r.EndLoss, "0"))} · audio−vídeo {Stats(r => r.AudioMinusVideo, "0.000")} s");
            if (reports is not null) report.Add("(«perdidos al final»: escritos − leídos − 1; el FFmpeg que entra a mitad del flujo descarta siempre el primer frame tras su punto de sincronía de arranque.)");
            foreach (var l in report) _out.WriteLine(l);
            if (Env("BAIOSS_STOP_TAIL_LOG") is { } log) File.AppendAllLines(log, report.Append(""));
            if (Env("BAIOSS_STOP_TAIL_ENGINE_LOG") is { } engineLog) lock (lines) File.WriteAllLines(engineLog, lines);
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Paquetes de vídeo que leyó de su entrada el FFmpeg que escribió <paramref name="file"/>, según su informe FFREPORT.</summary>
    private static long ReadVideoPackets(string reports, string file)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        string name = Path.GetFileName(file);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var log in Directory.GetFiles(reports, "*.log"))
            {
                string text;
                try { text = File.ReadAllText(log); } catch (IOException) { continue; }
                if (!text.Contains(name, StringComparison.Ordinal)) continue;
                var m = Regex.Match(text, @"Input stream #0:0 \(video\): (\d+) packets read");
                if (m.Success) return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            Thread.Sleep(200);
        }
        return -1;
    }

    /// <summary>Paquetes de vídeo del archivo y duraciones de vídeo y audio.</summary>
    private static async Task<(long VideoPackets, double Video, double Audio)> ProbeAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-count_packets", "-show_entries", "stream=codec_type,nb_read_packets,duration", "-of", "csv=p=0", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string s = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        long packets = 0; double video = double.NaN, audio = double.NaN;
        foreach (var line in s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d);
            if (parts[0] == "video") { video = d; long.TryParse(parts[2], out packets); }
            else if (parts[0] == "audio") audio = d;
        }
        return (packets, video, audio);
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
}
