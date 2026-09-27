using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Pruebas de ESTRÉS sobre el motor real (varios minutos; se activan con <c>BAIOSS_STRESS=1</c>): varios canales
/// grabando a la vez con segmentación, finalización y clips; ciclos rápidos de Grabar/Detener; caídas provocadas del
/// proceso de grabación; y una entrada de red a ráfagas con relevos, clips y caídas del emisor. Cada escenario, además
/// de comprobar los archivos, vigila las FUGAS: procesos ffmpeg huérfanos, hilos, descriptores y memoria del proceso de
/// pruebas antes y después, y temporales de remux que queden en las carpetas.
/// </summary>
public sealed class StressTests
{
    private readonly ITestOutputHelper _out;
    public StressTests(ITestOutputHelper output) { _out = output; }

    private static bool Enabled => Environment.GetEnvironmentVariable("BAIOSS_STRESS") is { Length: > 0 };

    private sealed record Snapshot(int Ffmpeg, int Threads, int Handles, long WorkingSetMb, long ManagedMb)
    {
        public static Snapshot Take()
        {
            var me = Process.GetCurrentProcess();
            me.Refresh();
            return new(Process.GetProcessesByName("ffmpeg").Length, me.Threads.Count, me.HandleCount,
                me.WorkingSet64 / 1048576, GC.GetTotalMemory(forceFullCollection: true) / 1048576);
        }
        public override string ToString() => $"ffmpeg {Ffmpeg} · hilos {Threads} · descriptores {Handles} · WS {WorkingSetMb} MB · gestionada {ManagedMb} MB";
    }

    /// <summary>Espera a que los ffmpeg hijos terminen tras disponer los motores y compara con la línea base.</summary>
    private async Task AssertNoLeaksAsync(string scenario, Snapshot before)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (Process.GetProcessesByName("ffmpeg").Length > before.Ffmpeg && DateTime.UtcNow < deadline) await Task.Delay(500);
        await Task.Delay(1500); // hilos lectores y del pool que aún estén cerrando
        var after = Snapshot.Take();
        _out.WriteLine($"{scenario} · antes: {before}");
        _out.WriteLine($"{scenario} · después: {after}");
        Assert.True(after.Ffmpeg <= before.Ffmpeg, $"quedan {after.Ffmpeg - before.Ffmpeg} ffmpeg huérfanos");
        Assert.True(after.Threads <= before.Threads + 12, $"hilos: {before.Threads} → {after.Threads}");
        Assert.True(after.Handles <= before.Handles + 300, $"descriptores: {before.Handles} → {after.Handles}");
        Assert.True(after.ManagedMb <= before.ManagedMb + 60, $"memoria gestionada: {before.ManagedMb} → {after.ManagedMb} MB");
    }

    private static RecordingProfile Profile(ContainerFormat container, int? segmentSeconds) => new()
    {
        Name = "stress", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(3), GopSize = 25,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = container,
        Segmentation = segmentSeconds is { } s ? new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromSeconds(s) } : null,
    };

    private static FileCaptureSource ClipSource() => new(new InputSource
    {
        Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
        Parameters = { ["loop"] = "1", ["realtime"] = "1" },
        ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
    });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition()) { if (sw.Elapsed > timeout) throw new TimeoutException(what); await Task.Delay(250); }
    }

    private static string[] Pieces(string root, string ext) => Directory.GetFiles(root, "*." + ext).Where(f => !f.Contains("_clip_")).OrderBy(f => f).ToArray();

    private static async Task<int> CountPlayableAsync(FfmpegLocator locator, IEnumerable<string> files)
    {
        int ok = 0;
        foreach (var f in files) { var p = await locator.ProbeMediaAsync(f); if (p.IsPlayable && p.DurationSeconds > 0.5) ok++; }
        return ok;
    }

    // -----------------------------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task FourChannels_Segmented_WithClipsAndFinalization_ForTwoMinutes()
    {
        Skip.IfNot(Enabled && TestAssets.Available, "estrés: define BAIOSS_STRESS=1 (y FFmpeg en tools/)");
        var before = Snapshot.Take();
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        const int channels = 4;
        var roots = Enumerable.Range(0, channels).Select(i => Path.Combine(Path.GetTempPath(), $"baioss-stress-{i}-{Guid.NewGuid():N}")).ToArray();
        var engines = new FfmpegChannelEngine[channels];
        var sources = new FileCaptureSource[channels];
        var problems = new ConcurrentBag<string>();
        var clipsOk = new ConcurrentBag<string>();
        var clipsRejected = new ConcurrentDictionary<ClipError, int>();
        try
        {
            for (int i = 0; i < channels; i++)
            {
                sources[i] = ClipSource();
                await sources[i].OpenAsync();
                var engine = engines[i] = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = roots[i], FragmentedMp4 = true };
                string key = ((char)('A' + i)).ToString();
                engine.RecordingInterrupted += (_, e) => problems.Add($"{key}: grabación interrumpida ({e.ExitCode}: {e.Reason})");
                engine.FileUnverified += (_, e) => problems.Add($"{key}: archivo sin verificar {Path.GetFileName(e.FilePath)}");
                await engine.StartPreviewAsync(sources[i], Profile(ContainerFormat.Mp4, 5), key);
            }
            await Task.WhenAll(engines.Select(e => e.StartRecordingAsync(Guid.NewGuid(), Profile(ContainerFormat.Mp4, 5))));

            // 120 s: un clip cada 4 s rotando de canal (10 s), y a los 40 s tres a la vez sobre el canal A (solo uno debe entrar).
            var sw = Stopwatch.StartNew();
            var clipTasks = new List<Task>();
            int n = 0;
            async Task ClipAsync(FfmpegChannelEngine engine, int seconds)
            {
                try { var c = await engine.ExtractClipAsync(TimeSpan.FromSeconds(seconds)); clipsOk.Add(c.FilePath); }
                catch (ClipExtractionException ex) when (ex.Error is ClipError.Busy or ClipError.TooShort) { clipsRejected.AddOrUpdate(ex.Error, 1, (_, v) => v + 1); }
                catch (Exception ex) { problems.Add("clip: " + ex.Message); }
            }
            while (sw.Elapsed < TimeSpan.FromSeconds(120))
            {
                await Task.Delay(4000);
                clipTasks.Add(ClipAsync(engines[n++ % channels], 10));
                if (n == 10) for (int k = 0; k < 3; k++) clipTasks.Add(ClipAsync(engines[0], 20));
                foreach (var e in engines) if (e.State != RecordingState.Recording) problems.Add($"estado {e.State} a los {sw.Elapsed.TotalSeconds:0} s");
            }
            await Task.WhenAll(clipTasks);
            await Task.WhenAll(engines.Select(e => e.StopRecordingAsync()));

            // Todo finalizado y reproducible; sin temporales; clips bien.
            foreach (var root in roots)
            {
                var pieces = Pieces(root, "mp4");
                Assert.True(pieces.Length >= 20, $"{Path.GetFileName(root)}: solo {pieces.Length} segmentos");
                await WaitUntilAsync(() => pieces.All(f => Mp4Layout.Inspect(f).IsFinalized), TimeSpan.FromSeconds(90),
                    $"{Path.GetFileName(root)}: segmentos sin finalizar: {pieces.Count(f => !Mp4Layout.Inspect(f).IsFinalized)}");
                int playable = await CountPlayableAsync(locator, pieces.Where((_, i) => i % 3 == 0));
                Assert.Equal(pieces.Where((_, i) => i % 3 == 0).Count(), playable);
                Assert.Empty(Directory.GetFiles(root, "*" + FfmpegLocator.RemuxTempSuffix));
            }
            var clips = clipsOk.ToArray();
            _out.WriteLine($"clips: {clips.Length} correctos · rechazados: {string.Join(", ", clipsRejected.Select(kv => $"{kv.Key} ×{kv.Value}"))}");
            Assert.True(clips.Length >= 25, $"solo {clips.Length} clips");
            Assert.All(clips, c => Assert.True(Mp4Layout.Inspect(c).IsFinalized, Path.GetFileName(c)));
            Assert.Equal(clips.Length, await CountPlayableAsync(locator, clips));
            Assert.True(clipsRejected.GetValueOrDefault(ClipError.Busy) >= 1, "tres clips a la vez sobre un canal: alguno debía rechazarse por ocupado");
            Assert.True(problems.IsEmpty, string.Join("\n", problems));
        }
        finally
        {
            foreach (var e in engines) if (e is not null) await e.DisposeAsync();
            foreach (var s in sources) if (s is not null) await s.DisposeAsync();
        }
        await AssertNoLeaksAsync("4 canales", before);
        foreach (var root in roots) try { Directory.Delete(root, true); } catch { /* best effort */ }
    }

    [SkippableFact]
    public async Task RapidStartStopCycles_LeaveNoLeaks_AndEveryPieceFinalized()
    {
        Skip.IfNot(Enabled && TestAssets.Available, "estrés: define BAIOSS_STRESS=1 (y FFmpeg en tools/)");
        var before = Snapshot.Take();
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var root = Path.Combine(Path.GetTempPath(), $"baioss-stress-cycles-{Guid.NewGuid():N}");
        var problems = new ConcurrentBag<string>();
        try
        {
            var source = ClipSource();
            await source.OpenAsync();
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = root, FragmentedMp4 = true };
            engine.RecordingInterrupted += (_, e) => problems.Add($"interrumpida ({e.ExitCode}: {e.Reason})");
            engine.FileUnverified += (_, e) => problems.Add($"sin verificar {Path.GetFileName(e.FilePath)}");
            await engine.StartPreviewAsync(source, Profile(ContainerFormat.Mp4, null), "CYC");
            const int cycles = 20;
            int tooShort = 0, clipsOk = 0;
            var handles = new List<int>();
            for (int i = 0; i < cycles; i++)
            {
                await engine.StartRecordingAsync(Guid.NewGuid(), Profile(ContainerFormat.Mp4, null));
                await Task.Delay(i % 2 == 0 ? 3000 : 8000);
                try { await engine.ExtractClipAsync(TimeSpan.FromSeconds(5)); clipsOk++; }
                catch (ClipExtractionException ex) when (ex.Error == ClipError.TooShort) { tooShort++; }
                await engine.StopRecordingAsync();
                Assert.Equal(RecordingState.Idle, engine.State);
                var me = Process.GetCurrentProcess(); me.Refresh(); handles.Add(me.HandleCount);
            }
            var pieces = Pieces(root, "mp4");
            _out.WriteLine($"ciclos: {cycles} · piezas {pieces.Length} · clips {clipsOk} · demasiado cortos {tooShort}");
            _out.WriteLine("descriptores por ciclo: " + string.Join(" ", handles));
            // Sin fuga por ciclo: la segunda mitad de los ciclos no debe seguir subiendo de forma sostenida.
            int firstHalf = handles[cycles / 2 - 1], last = handles[^1];
            Assert.True(last - firstHalf <= 60, $"los descriptores siguen creciendo: {firstHalf} → {last} en {cycles / 2} ciclos");
            Assert.Equal(cycles, pieces.Length);
            await WaitUntilAsync(() => pieces.All(f => Mp4Layout.Inspect(f).IsFinalized), TimeSpan.FromSeconds(60), "piezas sin finalizar");
            Assert.Equal(pieces.Length, await CountPlayableAsync(locator, pieces));
            Assert.True(clipsOk >= cycles / 2, $"clips correctos {clipsOk}");
            Assert.Empty(Directory.GetFiles(root, "*" + FfmpegLocator.RemuxTempSuffix));
            Assert.True(problems.IsEmpty, string.Join("\n", problems));
            await source.DisposeAsync();
        }
        finally { }
        await AssertNoLeaksAsync("ciclos", before);
        try { Directory.Delete(root, true); } catch { /* best effort */ }
    }

    [SkippableFact]
    public async Task RepeatedRecorderCrashes_AreRecoveredInNewPieces_AndEveryPieceStaysPlayable()
    {
        Skip.IfNot(Enabled && TestAssets.Available, "estrés: define BAIOSS_STRESS=1 (y FFmpeg en tools/)");
        var before = Snapshot.Take();
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var root = Path.Combine(Path.GetTempPath(), $"baioss-stress-crash-{Guid.NewGuid():N}");
        int interrupted = 0, unverified = 0;
        try
        {
            var source = ClipSource();
            await source.OpenAsync();
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = root, FragmentedMp4 = true };
            engine.RecordingInterrupted += (_, _) => Interlocked.Increment(ref interrupted);
            engine.FileUnverified += (_, _) => Interlocked.Increment(ref unverified);
            await engine.StartPreviewAsync(source, Profile(ContainerFormat.Mp4, 5), "CRS");
            await engine.StartRecordingAsync(Guid.NewGuid(), Profile(ContainerFormat.Mp4, 5));
            const int kills = 5;
            for (int k = 0; k < kills; k++)
            {
                await Task.Delay(8000);
                Assert.True(engine.KillRecorderProcessForTest(), "sin proceso que matar");
                await WaitUntilAsync(() => engine.State == RecordingState.Recording && Volatile.Read(ref interrupted) == k + 1, TimeSpan.FromSeconds(20), $"no se recuperó tras la caída {k + 1}");
            }
            await Task.Delay(6000);
            await engine.StopRecordingAsync();
            var pieces = Pieces(root, "mp4");
            _out.WriteLine($"caídas {kills} · piezas {pieces.Length} · interrumpidas {interrupted} · sin verificar {unverified}");
            foreach (var f in pieces)
            {
                var layout = Mp4Layout.Inspect(f);
                var probe = await locator.ProbeMediaAsync(f);
                _out.WriteLine($"  {Path.GetFileName(f)}: {new FileInfo(f).Length,10} B · {(layout.Fragmented ? "fMP4" : layout.IsFinalized ? "final" : "otro")} · {(probe.IsPlayable ? $"{probe.DurationSeconds:0.00} s" : "NO reproducible")}");
            }
            Assert.Equal(kills, interrupted);
            // Las piezas cortadas por la caída se conservan reproducibles (fMP4); las que la caída dejó sin contenido
            // (el proceso murió antes de escribir el primer fragmento) se descartan solas, sin alarma ni fila de segmento.
            Assert.True(pieces.Length >= kills, $"solo {pieces.Length} piezas");
            Assert.Equal(pieces.Length, await CountPlayableAsync(locator, pieces));
            await WaitUntilAsync(() => Directory.GetFiles(root, "*" + FfmpegLocator.RemuxTempSuffix).Length == 0, TimeSpan.FromSeconds(60), "temporales");
            int finalized = pieces.Count(f => Mp4Layout.Inspect(f).IsFinalized);
            _out.WriteLine($"finalizadas {finalized} de {pieces.Length}");
            Assert.True(finalized >= pieces.Length - kills, $"finalizadas {finalized} de {pieces.Length}");
            Assert.Equal(0, unverified);
            await source.DisposeAsync();
        }
        finally { }
        await AssertNoLeaksAsync("caídas", before);
        try { Directory.Delete(root, true); } catch { /* best effort */ }
    }

    [SkippableFact]
    public async Task BurstyRtmpSource_RecordStopCycles_WithClips_AndSenderDrops()
    {
        Skip.IfNot(Enabled && TestAssets.FfmpegDir is not null, "estrés: define BAIOSS_STRESS=1 (y FFmpeg en tools/)");
        var before = Snapshot.Take();
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var root = Path.Combine(Path.GetTempPath(), $"baioss-stress-net-{Guid.NewGuid():N}");
        const int senderPort = 19354;
        var problems = new ConcurrentBag<string>();
        var sender = NetworkPreviewContinuityTests.LightSender(senderPort);
        await using var proxy = new NetworkPreviewCushionTests.BurstyProxy(senderPort, TimeSpan.FromMilliseconds(600));
        try
        {
            await Task.Delay(1500);
            var input = new NetworkInput
            {
                Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = "127.0.0.1", Port = proxy.Port, Path = "live/test", PreviewBufferMs = 1200,
            }.ToInputSource(Guid.NewGuid(), "rtmp estrés");
            var factory = new NetworkStreamCaptureSourceFactory(locator, NullLoggerFactory.Instance);
            await using var source = (NetworkStreamCaptureSource)factory.Create(input);
            await source.OpenAsync();
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = root, FragmentedMp4 = true };
            var frames = new List<double>();
            var sw = Stopwatch.StartNew();
            engine.FrameReady += (_, _) => { lock (frames) frames.Add(sw.Elapsed.TotalSeconds); };
            engine.FileUnverified += (_, e) => problems.Add($"sin verificar {Path.GetFileName(e.FilePath)}");
            await engine.StartPreviewAsync(source, Profile(ContainerFormat.Mp4, null), "NET");
            await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(40), "sin señal");
            await Task.Delay(6000);

            const int cycles = 5;
            int clipsOk = 0, senderDrops = 0;
            var senderAliveWindows = new List<(double From, double To)>();
            double aliveFrom = sw.Elapsed.TotalSeconds;
            for (int i = 0; i < cycles; i++)
            {
                await engine.StartRecordingAsync(Guid.NewGuid(), Profile(ContainerFormat.Mp4, null));
                await Task.Delay(6000);
                try { await engine.ExtractClipAsync(TimeSpan.FromSeconds(8)); clipsOk++; }
                catch (ClipExtractionException ex) { problems.Add($"clip ciclo {i}: {ex.Error} {ex.Message}"); }
                if (i is 1 or 3)
                {
                    // El emisor se cae a mitad de grabación: el motor cierra la pieza y sigue en una nueva cuando vuelve.
                    senderAliveWindows.Add((aliveFrom, sw.Elapsed.TotalSeconds));
                    try { if (!sender.HasExited) sender.Kill(entireProcessTree: true); } catch { }
                    sender.Dispose();
                    senderDrops++;
                    await Task.Delay(3000);
                    sender = NetworkPreviewContinuityTests.LightSender(senderPort);
                    await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.Locked && engine.State == RecordingState.Recording, TimeSpan.FromSeconds(40), "no volvió tras la caída del emisor");
                    await Task.Delay(4000);
                    aliveFrom = sw.Elapsed.TotalSeconds;
                }
                await Task.Delay(4000);
                await engine.StopRecordingAsync();
                Assert.Equal(RecordingState.Idle, engine.State);
                await Task.Delay(2000);
            }
            senderAliveWindows.Add((aliveFrom, sw.Elapsed.TotalSeconds));

            // Preview sin huecos > 1 s mientras el emisor estuvo vivo (las ráfagas de 0,6 s las absorbe el colchón).
            double[] f; lock (frames) f = frames.ToArray();
            var gaps = new List<string>();
            for (int i = 1; i < f.Length; i++)
                if (f[i] - f[i - 1] > 1.0 && senderAliveWindows.Any(w => f[i - 1] >= w.From + 3 && f[i] <= w.To - 1))
                    gaps.Add($"{f[i] - f[i - 1]:0.00} s @{f[i - 1]:0.0} s");
            var pieces = Pieces(root, "mp4");
            _out.WriteLine($"ciclos {cycles} · caídas del emisor {senderDrops} · piezas {pieces.Length} · clips {clipsOk} · frames {f.Length} · huecos > 1 s con emisor vivo: {gaps.Count}");
            Assert.True(gaps.Count == 0, "huecos: " + string.Join(", ", gaps));
            Assert.True(pieces.Length >= cycles, $"piezas {pieces.Length}");
            await WaitUntilAsync(() => Directory.GetFiles(root, "*" + FfmpegLocator.RemuxTempSuffix).Length == 0, TimeSpan.FromSeconds(60), "temporales");
            Assert.Equal(pieces.Length, await CountPlayableAsync(locator, pieces));
            Assert.Equal(cycles, clipsOk);
            // Tras los relevos no quedan consumidores colgados del relé: solo el proceso de preview.
            await WaitUntilAsync(() => source.RelayConsumers == 1, TimeSpan.FromSeconds(30), $"consumidores del relé: {source.RelayConsumers}");
            Assert.True(problems.IsEmpty, string.Join("\n", problems));
        }
        finally
        {
            try { if (!sender.HasExited) sender.Kill(entireProcessTree: true); } catch { }
            sender.Dispose();
        }
        await AssertNoLeaksAsync("red", before);
        try { Directory.Delete(root, true); } catch { /* best effort */ }
    }
}
