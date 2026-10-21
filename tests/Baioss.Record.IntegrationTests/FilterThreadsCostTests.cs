using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Con BAIOSS_FILTER_THREADS=1: lo que cuesta el proceso de UN canal (núcleos) y si va al día (velocidad) según los hilos de su grafo de
/// filtros (<c>-filter_complex_threads</c>). La línea de comandos es la del motor (<see cref="FfmpegArgumentBuilder.BuildLive"/>, con la
/// cola de escritura y el MP4 estándar de la aplicación) y la entrada llega a ritmo real (<c>-re</c>) desde un volcado como el del
/// relé: a toda velocidad (<c>-benchmark</c>) el reparto entre hilos se comporta de otra manera y engaña. Los núcleos y la velocidad se
/// cuentan después de <see cref="SettleSeconds"/> s (el <c>-re</c> de FFmpeg 8.0 lee al 1,05× los primeros 10 s).
/// <list type="bullet">
///   <item>DeckLink: un NUT con vídeo en crudo uyvy422 1080i59.94 TFF y 16 canales PCM, lo que entrega el relé (BAIOSS_FT_DUMP; sin
///   él, uno sintético de 10 s, menos fiel: el coste depende de la imagen).</item>
///   <item>NDI como el grafismo de Offside: un cuadro cada 12 periodos de 60 (5 por segundo), en el NUT con marcas que escribe el
///   receptor (<see cref="NutRawVideoWriter"/>), con los cuadros del volcado; audio estéreo f32le en silencio en la entrada 1.</item>
/// </list>
/// Con BAIOSS_FT_DEVICE («DeckLink Duo (2)») los casos DeckLink leen la tarjeta en vivo por el relé, como la aplicación (16 canales de
/// audio): la imagen ya no es la misma en cada medida, pero el relé cuenta los cuadros que el canal no recoge a tiempo (descartes). Con
/// BAIOSS_FT_FEED=sintetica, la fuente sintética de FactoryPresetRecordingTests (testsrc2 tejido, generado en el mismo proceso).
/// Variables: BAIOSS_FT_THREADS (lista: «ffmpeg» = sin la opción, los de FFmpeg; «producto» = lo que emite el builder; o un número;
/// por defecto «ffmpeg,4,2»), BAIOSS_FT_ONLY (trozos de los nombres de los casos, separados por «|»), BAIOSS_FT_REPEAT (pasadas, 1),
/// BAIOSS_FT_SECONDS (ventana de medida, 30) y BAIOSS_FT_REPORT (archivo al que se añade una línea por medida). El orden alterna los
/// hilos dentro de cada caso para que la carga ajena no caiga siempre en la misma columna.
/// </summary>
public sealed class FilterThreadsCostTests
{
    private const double SettleSeconds = 15;
    private const int PreviewWidth = 640, PreviewHeight = 360;
    private readonly ITestOutputHelper _out;
    public FilterThreadsCostTests(ITestOutputHelper output) => _out = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <param name="Preset">Preset de fábrica que graba; null = solo preview (el canal sin grabar).</param>
    /// <param name="Copies">Canales iguales a la vez, cada uno con su proceso (dos entradas DeckLink grabando): núcleos sumados y la
    /// velocidad del más lento.</param>
    private sealed record Case(string Label, string? Preset, bool Ndi = false, int Copies = 1);

    private static readonly Case[] Cases =
    {
        new("Preview DeckLink 1080i", null),
        new("NVENC 1080p59.94 (bwdif por campo)", "H.264 NVENC · FullHD 1080p59.94"),
        new("2 canales NVENC 1080p59.94 (bwdif por campo)", "H.264 NVENC · FullHD 1080p59.94", Copies: 2),
        new("NVENC 2160p59.94 (bwdif por campo + 4K)", "H.264 NVENC · 4K UHD 2160p59.94"),
        new("ProRes 422 HQ 1080i59.94", "ProRes 422 HQ · 1080i59.94"),
        new("ProRes 422 HQ 1080p59.94 (bwdif por campo)", "ProRes 422 HQ · 1080p59.94"),
        new("x264 1080p59.94 (bwdif por campo)", "H.264 Broadcast · FullHD 1080p59.94"),
        new("2 canales x264 1080p59.94 (bwdif por campo, CPU llena)", "H.264 Broadcast · FullHD 1080p59.94", Copies: 2),
        new("x264 1080i59.94 MP4", "H.264 Broadcast · 1080i59.94 MP4"),
        new("XDCAM HD422 1080i59.94", "XDCAM HD422 · 1080i59.94 50 Mbps"),
        new("NDI como Offside → NVENC 1080p60", "H.264 NVENC · FullHD 1080p60", Ndi: true),
    };

    private sealed record Measure(string Case, string Threads, double Cores, double Speed, int OsThreads, long Drops, string Note);

    [SkippableFact]
    public async Task MeasureChannelCostByFilterThreads()
    {
        Skip.IfNot(Env("BAIOSS_FILTER_THREADS") == "1", "Medición: BAIOSS_FILTER_THREADS=1.");
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var settings = (Env("BAIOSS_FT_THREADS") ?? "ffmpeg,4,2")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int repeat = int.Parse(Env("BAIOSS_FT_REPEAT") ?? "1", CultureInfo.InvariantCulture);
        double window = double.Parse(Env("BAIOSS_FT_SECONDS") ?? "30", CultureInfo.InvariantCulture);
        var cases = Env("BAIOSS_FT_ONLY") is { } only
            ? Cases.Where(c => only.Split('|').Any(o => c.Label.Contains(o, StringComparison.OrdinalIgnoreCase))).ToArray()
            : Cases;
        var work = Path.Combine(Path.GetTempPath(), $"baioss-ft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var results = new List<Measure>();
        DecklinkCaptureSource? live = null;
        long drops = 0;
        try
        {
            if (Env("BAIOSS_FT_DEVICE") is { } device)
            {
                var definition = new InputSource
                {
                    Name = $"DeckLink — {device}", Type = InputType.DecklinkSdi, Uri = device,
                    Parameters = { [AudioSelection.ChannelsKey] = "16" },
                };
                live = (DecklinkCaptureSource)new DecklinkCaptureSourceFactory(locator, NullLoggerFactory.Instance, relay: true)
                    .Create(definition);
                live.InputDataDropped += (_, n) => Interlocked.Add(ref drops, n);
                await live.OpenAsync();
                var deadline = Stopwatch.StartNew();
                while (live.CurrentSignal.State != SignalState.Locked && deadline.Elapsed < TimeSpan.FromSeconds(20))
                    await Task.Delay(200);
                Skip.IfNot(live.CurrentSignal.State == SignalState.Locked, $"{device}: sin señal ({live.CurrentSignal.FormatLabel}).");
                _out.WriteLine($"En vivo: {device} · {live.CurrentSignal.FormatLabel} · {live.CurrentSignal.AudioChannels} canales de audio");
            }
            // En vivo, el volcado solo hace falta para los cuadros del NDI.
            string dump = live is not null && !cases.Any(c => c.Ndi)
                ? ""
                : Env("BAIOSS_FT_DUMP") ?? await SyntheticDeckLinkDumpAsync(locator, work);
            string? ndiVideo = null, ndiAudio = null;
            if (cases.Any(c => c.Ndi)) (ndiVideo, ndiAudio) = await NdiDumpAsync(locator, dump, work, SettleSeconds + window + 15);
            for (int pass = 0; pass < repeat; pass++)
                foreach (var c in cases)
                    foreach (var threads in pass % 2 == 0 ? settings : Enumerable.Reverse(settings))
                    {
                        ICaptureSource feed = c.Ndi ? new DumpFeed(ndiVideo!, ndiAudio)
                            : live is not null ? live
                            : Env("BAIOSS_FT_FEED") == "sintetica" ? new LiveFeed(1920, 1080, FrameRate.P2997, ScanType.InterlacedTff, "uyvy422")
                            : new DumpFeed(dump, null);
                        long dropsBefore = Interlocked.Read(ref drops);
                        var m = await RunAsync(locator, c, threads, feed, work, window);
                        m = m with { Drops = Interlocked.Read(ref drops) - dropsBefore };
                        results.Add(m);
                        string line = string.Create(CultureInfo.InvariantCulture,
                            $"{m.Case}\t{m.Threads}\t{m.Cores:0.00} núcleos\t{m.Speed:0.000}×\t{m.OsThreads} hilos\t{m.Drops} descartes");
                        if (m.Note.Length > 0) line += "\t" + m.Note;
                        _out.WriteLine(line);
                        if (Env("BAIOSS_FT_REPORT") is { } report)
                            File.AppendAllText(report, $"{DateTime.Now:HH:mm:ss}\t{line}{Environment.NewLine}");
                    }
        }
        finally
        {
            if (live is not null) await live.DisposeAsync();
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }

        _out.WriteLine("");
        _out.WriteLine("Mediana por caso (núcleos · velocidad):");
        foreach (var c in cases)
            _out.WriteLine($"{c.Label}: " + string.Join(" | ", settings.Select(t =>
            {
                var rows = results.Where(r => r.Case == c.Label && r.Threads == t).ToList();
                return rows.Count == 0
                    ? $"{t}: —"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"{t}: {Median(rows.Select(r => r.Cores)):0.00} · {Median(rows.Select(r => r.Speed)):0.000}×");
            })));
        Assert.All(results, r => Assert.True(r.Speed > 0, $"{r.Case} ({r.Threads}): no avanzó. {r.Note}"));
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length == 0 ? double.NaN : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    /// <summary>La línea del canal con los hilos del grafo pedidos: «ffmpeg» la deja sin la opción, «producto» como la construye el
    /// builder, un número la fija.</summary>
    internal static List<string> WithFilterThreads(IReadOnlyList<string> args, string setting)
    {
        var list = new List<string>(args);
        if (setting.Equals("producto", StringComparison.OrdinalIgnoreCase)) return list;
        int at = list.IndexOf("-filter_complex_threads");
        if (at >= 0) list.RemoveRange(at, 2);
        if (int.TryParse(setting, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            list.InsertRange(1, new[] { "-filter_complex_threads", n.ToString(CultureInfo.InvariantCulture) });
        return list;
    }

    private static async Task<Measure> RunAsync(FfmpegLocator locator, Case c, string threads, ICaptureSource feed, string work, double window)
    {
        if (feed is DumpFeed or LiveFeed) await feed.OpenAsync();
        var preset = PresetCatalog.CreateBuiltIns().Single(p => p.Name == (c.Preset ?? "H.264 Broadcast · 1080i59.94 MP4"));
        var queue = await locator.SupportsOutputThreadQueueAsync() ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        var clock = Stopwatch.StartNew();
        var channels = new List<Channel>();
        string note = "";
        double cores = double.NaN, speed = double.NaN;
        int osThreads = 0;
        try
        {
            for (int i = 0; i < c.Copies; i++)
                channels.Add(Channel.Start(locator, feed, preset.ToProfile(), recording: c.Preset is not null, threads, queue, work, clock));
            await Task.Delay(TimeSpan.FromSeconds(SettleSeconds));
            if (channels.FirstOrDefault(ch => ch.Process.HasExited) is { } early) throw new InvalidOperationException($"FFmpeg salió con {early.Process.ExitCode}");
            double t0 = clock.Elapsed.TotalSeconds;
            double cpu0 = channels.Sum(ch => ch.Process.TotalProcessorTime.TotalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(window));
            if (channels.FirstOrDefault(ch => ch.Process.HasExited) is { } late) throw new InvalidOperationException($"FFmpeg salió con {late.Process.ExitCode}");
            double t1 = clock.Elapsed.TotalSeconds;
            double cpu1 = channels.Sum(ch => ch.Process.TotalProcessorTime.TotalSeconds);
            cores = (cpu1 - cpu0) / (t1 - t0);
            speed = channels.Min(ch => ch.Speed(t0, t1));
            channels[0].Process.Refresh();
            osThreads = channels[0].Process.Threads.Count;
        }
        catch (Exception e)
        {
            note = $"{e.Message}: {string.Join(" / ", channels.SelectMany(ch => ch.LastErrors()).TakeLast(6))}";
        }
        finally
        {
            foreach (var ch in channels) note += await ch.StopAsync();
        }
        return new Measure(c.Label, threads, cores, speed, osThreads, 0, note);
    }

    /// <summary>El proceso de un canal: la línea del motor para esa fuente y ese preset, con su preview a un lector que lo tira (la
    /// aplicación lo lee y lo pinta) y su progreso (cada bloque, uno por segundo, con su instante de llegada y el tiempo de medios).</summary>
    private sealed class Channel
    {
        private readonly List<(double At, long OutUs)> _blocks = new();
        private readonly Queue<string> _errors = new();
        private readonly CancellationTokenSource _cts = new();
        private TcpListener _sink = null!;
        private string _dir = "";
        private Task _io = Task.CompletedTask;
        public Process Process { get; private set; } = null!;

        public static Channel Start(FfmpegLocator locator, ICaptureSource feed, RecordingProfile profile, bool recording, string threads,
            TimeSpan writeQueue, string work, Stopwatch clock)
        {
            var ch = new Channel { _dir = Path.Combine(work, $"rec-{Guid.NewGuid():N}") };
            Directory.CreateDirectory(ch._dir);
            ch._sink = new TcpListener(IPAddress.Loopback, 0);
            ch._sink.Start();
            var args = new FfmpegArgumentBuilder()
                .From(feed).Using(profile).ForChannel("FT").ToDirectory(ch._dir)
                .WithPreviewSink($"tcp://127.0.0.1:{((IPEndPoint)ch._sink.LocalEndpoint).Port}")
                .WithWriteQueue(writeQueue).WithFragmentedMp4(false)
                .BuildLive(recording, PreviewWidth, PreviewHeight);
            var psi = new ProcessStartInfo(locator.FfmpegPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = ch._dir,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in WithFilterThreads(args, threads)) psi.ArgumentList.Add(a);
            ch.Process = Process.Start(psi)!;
            var drain = Task.Run(async () =>
            {
                try
                {
                    using var client = await ch._sink.AcceptTcpClientAsync(ch._cts.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[1 << 20];
                    while (await stream.ReadAsync(buffer, ch._cts.Token) > 0) { }
                }
                catch { /* fin */ }
            });
            var progress = Task.Run(async () =>
            {
                long outUs = -1;
                while (await ch.Process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)) outUs = us;
                    else if (line.StartsWith("progress=", StringComparison.Ordinal) && outUs > 0)
                        lock (ch._blocks) ch._blocks.Add((clock.Elapsed.TotalSeconds, outUs));
                }
            });
            var stderr = Task.Run(async () =>
            {
                while (await ch.Process.StandardError.ReadLineAsync() is { } line)
                    lock (ch._errors) { ch._errors.Enqueue(line); if (ch._errors.Count > 40) ch._errors.Dequeue(); }
            });
            ch._io = Task.WhenAll(drain, progress, stderr);
            return ch;
        }

        /// <summary>Medios escritos por segundo de reloj entre los bloques de progreso que caen en [t0, t1].</summary>
        public double Speed(double t0, double t1)
        {
            lock (_blocks)
            {
                var inWindow = _blocks.Where(b => b.At >= t0 && b.At <= t1).ToList();
                return inWindow.Count >= 2 ? (inWindow[^1].OutUs - inWindow[0].OutUs) / 1e6 / (inWindow[^1].At - inWindow[0].At) : double.NaN;
            }
        }

        public IReadOnlyList<string> LastErrors() { lock (_errors) return _errors.TakeLast(6).ToList(); }

        /// <summary>«q», como el motor; si no sale en 20 s, se mata. Devuelve una nota si hubo que matarlo.</summary>
        public async Task<string> StopAsync()
        {
            string note = "";
            try { await Process.StandardInput.WriteAsync("q"); await Process.StandardInput.FlushAsync(); } catch { /* ya salió */ }
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await Process.WaitForExitAsync(exit.Token); }
            catch (OperationCanceledException) { try { Process.Kill(entireProcessTree: true); } catch { } note = " (no salió con «q»)"; }
            _cts.Cancel();
            _sink.Stop();
            await _io.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
            Process.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
            return note;
        }
    }

    /// <summary>Sin volcado real: 10 s de testsrc2 tejido en 4:2:2 (1080i59.94 TFF, como una tarjeta) en uyvy422, con un tono en el par
    /// 1-2 de 16 canales PCM: el formato del relé.</summary>
    private static async Task<string> SyntheticDeckLinkDumpAsync(FfmpegLocator locator, string work)
    {
        var path = Path.Combine(work, "decklink-sintetico.nut");
        await RecordedFileCheck.RunAsync(locator.FfmpegPath, "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60000/1001,format=yuv422p,tinterlace=mode=interleave_top,format=uyvy422",
            "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
            "-t", "10", "-map", "0:v", "-map", "1:a", "-c:v", "rawvideo", "-c:a", "pcm_s16le", "-af", "pan=16c|c0=c0|c1=c0",
            "-f", "nut", path);
        return path;
    }

    /// <summary>El NUT de una fuente NDI como Offside: <paramref name="seconds"/> s, un cuadro cada 12 periodos de 60 (los del volcado,
    /// en bucle), con el escritor del receptor; y 10 s de silencio estéreo f32le para la entrada de audio, que va en bucle.</summary>
    private static async Task<(string Video, string Audio)> NdiDumpAsync(FfmpegLocator locator, string dump, string work, double seconds)
    {
        const int width = 1920, height = 1080, everyPeriods = 12;
        int frames = (int)Math.Ceiling(seconds * 60 / everyPeriods);
        var video = Path.Combine(work, "ndi-offside.nut");
        var audio = Path.Combine(work, "ndi-silencio.f32");
        await File.WriteAllBytesAsync(audio, new byte[48000 * 2 * 4 * 10]);
        var psi = new ProcessStartInfo(locator.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-stream_loop", "-1", "-i", dump, "-map", "0:v", "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
                     "-f", "rawvideo", "-pix_fmt", "uyvy422", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        var writer = new NutRawVideoWriter(width, height, "uyvy422", 60, 1);
        var frame = new byte[width * height * 2];
        var header = new byte[NutRawVideoWriter.MaxFrameHeaderBytes];
        await using (var file = File.Create(video))
        {
            await file.WriteAsync(writer.Preamble);
            var input = p.StandardOutput.BaseStream;
            for (int n = 0; n < frames; n++)
            {
                await input.ReadExactlyAsync(frame);
                int h = writer.WriteFrameHeader(header, (long)n * everyPeriods, frame.Length);
                await file.WriteAsync(header.AsMemory(0, h));
                await file.WriteAsync(frame);
            }
        }
        await p.WaitForExitAsync();
        return (video, audio);
    }

    /// <summary>Una entrada leída de un volcado a ritmo real: el NUT del relé DeckLink (vídeo y 16 canales PCM en la entrada 0) o, con
    /// <paramref name="ndiAudio"/>, el de NDI (solo los cuadros que llegan, a 60 declarados) con su audio en la entrada 1.</summary>
    private sealed class DumpFeed(string video, string? ndiAudio) : ICaptureSource
    {
        private bool Ndi => ndiAudio is not null;
        public InputSource Definition { get; } = new() { Name = "volcado", Type = InputType.File };
        public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
        public event EventHandler<SignalInfo>? SignalChanged;
        public bool DeliversInRealTime => true;
        public bool DeliversSparseVideo => Ndi;
        public int AudioInputIndex => Ndi ? 1 : 0;
        public int AudioChannelCount => Ndi ? 2 : 16;

        public Task OpenAsync(CancellationToken ct = default)
        {
            CurrentSignal = Ndi
                ? new SignalInfo(SignalState.Locked, Resolution.Hd1080, new FrameRate(60, 1), AudioLayout.Stereo, HasAudio: true, null, null,
                    Scan: ScanType.Progressive)
                : new SignalInfo(SignalState.Locked, Resolution.Hd1080, FrameRate.P2997, AudioLayout.Stereo, HasAudio: true, null, null,
                    AudioChannels: 16, Scan: ScanType.InterlacedTff);
            SignalChanged?.Invoke(this, CurrentSignal);
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

        // Como el relé (ConsumerArgumentsFor) o el receptor NDI, más -re y el bucle.
        public IReadOnlyList<string> BuildInputArguments() => Ndi
            ? new[]
            {
                "-re", "-f", "nut", "-analyzeduration", "0", "-probesize", "32", "-i", video,
                "-re", "-stream_loop", "-1", "-f", "f32le", "-ar", "48000", "-ac", "2", "-analyzeduration", "0", "-probesize", "32", "-i", ndiAudio!,
            }
            : new[] { "-re", "-stream_loop", "-1", "-f", "nut", "-analyzeduration", "200000", "-probesize", "20000000", "-i", video };

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
