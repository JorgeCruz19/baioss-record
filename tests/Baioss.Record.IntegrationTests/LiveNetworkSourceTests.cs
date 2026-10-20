using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Con BAIOSS_LIVE_NET_URL (una o varias URL srt:// o rtmp:// separadas por «|», como se pegan en Entradas → Fuentes de red): cada
/// fuente REAL por el canal entero de la aplicación —receptor, relé, motor unificado y canal (pre-vuelo y alarmas)—:
/// <list type="bullet">
///   <item>cuánto tarda en dar SEÑAL OK y con qué formato, y el preview en régimen (cuadros por segundo, huecos, ráfagas, CPU), con
///   el colchón de preview de BAIOSS_LIVE_NET_CUSHION_MS (0 = sin colchón, como viene una fuente nueva);</item>
///   <item>BAIOSS_LIVE_NET_CYCLES (2) Grabar/Detener de BAIOSS_LIVE_NET_SECONDS (15) s con cada preset de BAIOSS_LIVE_NET_PRESETS
///   («H.264 NVENC · FullHD 1080p29.97|H.264 Broadcast · FullHD 1080p29.97»): el relevo de proceso no congela ni acelera el preview
///   más de lo que ya lo hace la propia fuente (una RTMP que llega a ráfagas tiene huecos y ráfagas sin que nadie grabe); cada archivo
///   se relee entero (<see cref="RecordedFileCheck"/>), lleva el pre-roll de la red delante y empieza con el audio y el vídeo juntos;
///   sin alarmas de frames perdidos, archivos sin verificar ni grabaciones interrumpidas;</item>
///   <item>un hipo de la red a mitad de grabación (el receptor congelado BAIOSS_LIVE_NET_STALL_SECONDS, 6 s, como un enlace que se
///   atasca) y un corte (el receptor muere, como una conexión que se cae): qué hizo la señal, cuánto estuvo el preview sin cuadros y
///   cuánto tardó en volver, en cuántas piezas siguió la grabación y cuánto contenido se perdió; cada pieza se decodifica entera con
///   el audio tan largo como el vídeo.</item>
/// </list>
/// Resumen en BAIOSS_LIVE_NET_REPORT; BAIOSS_LIVE_NET_KEEP=1 conserva los archivos. La URL puede llevar la contraseña SRT: el informe
/// y el registro solo nombran protocolo, dirección y puerto (y cualquier línea que la trajera sale tapada).
/// </summary>
public sealed class LiveNetworkSourceTests
{
    private readonly ITestOutputHelper _out;
    public LiveNetworkSourceTests(ITestOutputHelper output) => _out = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
    private static double Number(string name, double fallback) => Env(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    /// <summary>Lo que se observa tras un hipo o un corte antes de Detener: lo que tarda la reconexión SRT (1 s de espera del supervisor
    /// y el análisis del receptor, hasta un GOP; ~7 s antes del 2026-10-20) y la pieza nueva.</summary>
    private const double CutObservationSeconds = 20;

    [SkippableFact]
    public async Task RealSources_LockPreviewAndRecord_AndSurviveAStallAndACut()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        Skip.If(Env("BAIOSS_LIVE_NET_URL") is null, "Fuentes de red reales: BAIOSS_LIVE_NET_URL=srt://…|rtmp://…");
        var urls = Env("BAIOSS_LIVE_NET_URL")!.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var catalog = PresetCatalog.CreateBuiltIns();
        var presets = (Env("BAIOSS_LIVE_NET_PRESETS") ?? "H.264 NVENC · FullHD 1080p29.97|H.264 Broadcast · FullHD 1080p29.97")
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => catalog.Single(p => p.Name == n)).ToList();
        var plan = new Plan(presets, (int)Number("BAIOSS_LIVE_NET_CYCLES", 2), Number("BAIOSS_LIVE_NET_SECONDS", 15),
            Number("BAIOSS_LIVE_NET_STALL_SECONDS", 6), Env("BAIOSS_LIVE_NET_CUTS") ?? "stall,kill");
        int cushion = (int)Number("BAIOSS_LIVE_NET_CUSHION_MS", 0);
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var queue = await locator.SupportsOutputThreadQueueAsync() ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;

        var report = new List<string> { $"## Fuentes de red reales · {DateTime.Now:yyyy-MM-dd HH:mm} · colchón de preview {cushion} ms" };
        var problems = new List<string>();
        foreach (var url in urls)
        {
            Assert.True(NetworkInput.TryParseUrl(url, out var parsed, out var error) && parsed is not null, $"URL no válida ({error})");
            var input = parsed! with { PreviewBufferMs = cushion };
            var run = new Run(input.Url, input.Passphrase); // Url sin opciones: ni la contraseña ni el streamid
            try { await ExerciseAsync(run, input, plan, locator, queue); }
            catch (Exception ex) { run.Problem($"la prueba se cortó: {ex.GetType().Name}: {ex.Message}"); }
            finally { run.Cleanup(); }
            report.AddRange(run.Lines());
            problems.AddRange(run.Problems.Select(p => $"{run.Label}: {p}"));
        }
        foreach (var l in report) _out.WriteLine(l);
        if (Env("BAIOSS_LIVE_NET_REPORT") is { } path) File.AppendAllLines(path, report.Append(""));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private sealed record Plan(List<EncodingPreset> Presets, int Cycles, double Seconds, double StallSeconds, string Cuts);

    private enum Cut { None, Stall, Kill }

    /// <summary>El preview de la fuente sin grabar: su hueco mayor y su ráfaga mayor por cuarto de segundo. Un relevo se juzga contra
    /// esto: lo que la fuente ya hace sola no es culpa del relevo.</summary>
    private sealed record Baseline(double Gap, int Burst)
    {
        public double GapLimit => Math.Max(0.5, Gap + 0.25);
        public int BurstLimit => Math.Max(19, Burst + 2);
    }

    private static async Task ExerciseAsync(Run run, NetworkInput input, Plan plan, FfmpegLocator locator, TimeSpan queue)
    {
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(run.Log);
        var factory = new NetworkStreamCaptureSourceFactory(locator, loggers);
        await using var source = (NetworkStreamCaptureSource)factory.Create(input.ToInputSource(Guid.NewGuid(), "fuente real"));
        source.SignalChanged += (_, s) => run.Signal(s.State, $"señal {s.State}{(s.FormatLabel is { } f ? " · " + f : "")}");
        var cpuSince = DateTime.Now;
        await source.OpenAsync();
        // Como la aplicación (ChannelHost): fMP4 y la cola de escritura si este FFmpeg la admite.
        await using var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine")) { OutputRoot = run.Dir, WriteQueue = queue };
        engine.FrameReady += (_, _) => run.Frame();
        engine.AlarmChanged += (_, a) => run.Event($"alarma del motor {a.Type} {(a.Active ? "activa" : "resuelta")}");
        engine.FileUnverified += (_, f) => run.Problem($"archivo sin verificar {Path.GetFileName(f.FilePath)}");
        engine.RecordingInterrupted += (_, i) => run.Interrupted($"{i.ExitCode} {i.Reason}");
        engine.SegmentClosed += (_, s) => run.Piece(s.FilePath);
        var first = plan.Presets[0].ToProfile();
        await engine.StartPreviewAsync(source, first, "NET");

        double deadline = run.Now + 40;
        while (source.CurrentSignal.State != SignalState.Locked && run.Now < deadline) await Task.Delay(100);
        if (source.CurrentSignal.State != SignalState.Locked) { run.Add($"### {run.Label}: sin SEÑAL OK en 40 s"); run.Problem("sin SEÑAL OK en 40 s"); return; }
        double lockedAt = run.Now;
        var signal = source.CurrentSignal;
        await Task.Delay(4000); // el preview en régimen
        double p0 = run.Now, cpu0 = RecordedFileCheck.FfmpegCpuSeconds(cpuSince);
        await Task.Delay(15000);
        double p1 = run.Now, cpu1 = RecordedFileCheck.FfmpegCpuSeconds(cpuSince);
        var (baseGap, baseBurst) = run.Around(p0, p0, p1);
        var baseline = new Baseline(baseGap, baseBurst);
        run.Add($"### {run.Label} · {signal.FormatLabel} · audio {signal.AudioChannels} c · SEÑAL OK a los {lockedAt:0.0} s");
        run.Add($"- Preview en régimen: {run.PreviewSummary(p0, p1)} · receptor + preview {(cpu1 - cpu0) / (p1 - p0):0.00} núcleos" +
                $" · en los relevos se admite un hueco de {baseline.GapLimit:0.00} s y {baseline.BurstLimit} cuadros por 250 ms");

        await using var channel = new StandaloneChannelEngine("NET", source, first, engine, log: new Logger<StandaloneChannelEngine>(loggers));
        var extras = new List<double>(); // pre-roll + cola tras Detener de cada grabación sin cortes: lo que se espera de más
        foreach (var preset in plan.Presets)
        {
            channel.Profile = preset.ToProfile(); // como al aplicar un preset en el panel
            for (int i = 1; i <= plan.Cycles; i++)
                await RecordAsync(run, channel, engine, source, locator, preset, $"{preset.Name} #{i}", plan, signal, baseline, Cut.None, extras);
        }
        channel.Profile = first;
        if (plan.Cuts.Contains("stall", StringComparison.OrdinalIgnoreCase))
            await RecordAsync(run, channel, engine, source, locator, plan.Presets[0], $"hipo de {plan.StallSeconds:0} s", plan, signal, baseline, Cut.Stall, extras);
        if (plan.Cuts.Contains("kill", StringComparison.OrdinalIgnoreCase))
            await RecordAsync(run, channel, engine, source, locator, plan.Presets[0], "corte", plan, signal, baseline, Cut.Kill, extras);
    }

    /// <summary>Una grabación de Grabar a Detener (con un hipo o un corte de la red por medio, si se pide) y lo que dejó.</summary>
    private static async Task RecordAsync(Run run, StandaloneChannelEngine channel, FfmpegChannelEngine engine, NetworkStreamCaptureSource source,
        FfmpegLocator locator, EncodingPreset preset, string name, Plan plan, SignalInfo signal, Baseline baseline, Cut cut, List<double> extras)
    {
        var profile = channel.Profile;
        var problems = new List<string>();
        var alarms = new HashSet<string>();
        long dropped = 0;
        int piecesBefore = run.PieceCount;
        int interruptionsBefore = run.InterruptionCount;
        async Task HoldAsync(double seconds)
        {
            double until = run.Now + seconds;
            while (run.Now < until)
            {
                await Task.Delay(250);
                var status = channel.Status;
                foreach (var a in status.Alarms ?? Array.Empty<ChannelAlarm>()) alarms.Add(a.Type.ToString());
                if (status.RecordingState == RecordingState.Recording) dropped = Math.Max(dropped, status.Stats.DroppedFrames);
            }
        }

        double tStart = run.Now;
        await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
        double started = run.Now;
        string cutText = "";
        if (cut == Cut.None) await HoldAsync(plan.Seconds);
        else
        {
            await HoldAsync(6);
            using var receiver = FindReceiver(run.Label);
            if (receiver is null) { problems.Add("no se encontró el FFmpeg receptor para cortarlo"); await HoldAsync(6); }
            else
            {
                double at = run.Now;
                if (cut == Cut.Stall)
                {
                    Suspend(receiver);
                    run.Event($"receptor congelado ({plan.StallSeconds:0} s)");
                    await HoldAsync(plan.StallSeconds);
                    Resume(receiver);
                    run.Event("receptor reanudado");
                }
                else { receiver.Kill(); run.Event("receptor muerto"); }
                double released = run.Now;
                // Se observa un rato largo y se analiza DESPUÉS: tras un hipo, el receptor puede soltar lo atrasado de golpe y luego
                // perder la conexión igualmente (SRT), así que «ya volvió» solo se sabe mirando hacia atrás.
                await HoldAsync(CutObservationSeconds);
                cutText = " · " + run.DescribeCut(cut == Cut.Stall ? "hipo" : "corte", at, released, run.Now, tStart, baseline.GapLimit);
                if (!run.PreviewFlowingAt(run.Now)) problems.Add($"el preview no volvió en {CutObservationSeconds:0} s");
                if (engine.State != RecordingState.Recording) problems.Add($"la grabación no siguió en {CutObservationSeconds:0} s ({engine.State})");
            }
        }
        double tStop = run.Now;
        await channel.StopRecordingAsync(RecordingStopReason.Operator);
        double stopped = run.Now;
        string? last = engine.LastOutputFile;
        await RecordedFileCheck.WaitForTheEngineAsync(run.LogsForWaiting, last, profile.Container);
        while (run.Now < tStop + 5.5) await Task.Delay(100); // el preview tras Detener, para medir el relevo
        var pieces = run.PiecesSince(piecesBefore);
        if (last is not null && !pieces.Contains(last)) pieces.Add(last);
        foreach (var p in pieces.Where(p => p != last)) await RecordedFileCheck.WaitForTheEngineAsync(run.LogsForWaiting, p, profile.Container);

        string Transition(string what, double t, double wait)
        {
            var (gap, burst) = run.AroundTransition(t);
            if (!(gap <= baseline.GapLimit)) problems.Add($"preview congelado {gap:0.00} s al {what}");
            if (burst > baseline.BurstLimit) problems.Add($"preview acelerado al {what} ({burst} cuadros en 250 ms)");
            return string.Create(CultureInfo.InvariantCulture, $"{what} {wait:0.00}/{gap:0.000}/{burst}");
        }
        string transitions = $"relevos (espera s/hueco máx s/ráfaga máx por 250 ms): {Transition("Grabar", tStart, started - tStart)} · {Transition("Detener", tStop, stopped - tStop)}";
        var lines = new List<string> { $"- {name} · {run.LastEncoder()} · {pieces.Count} pieza(s){cutText} · {transitions}" +
                                       $"{(dropped > 0 ? $" · {dropped} perdidos" : "")}{(alarms.Count > 0 ? $" · alarmas {string.Join(", ", alarms)}" : "")}" };
        double button = tStop - tStart;
        double total = 0;
        foreach (var file in pieces)
        {
            if (!File.Exists(file) || new FileInfo(file).Length == 0) { problems.Add($"pieza vacía o ausente {Path.GetFileName(file)}"); continue; }
            var size = profile.TargetResolution ?? signal.Resolution ?? new Resolution(1920, 1080);
            var rate = profile.OutputFrameRate ?? signal.FrameRate ?? FrameRate.P2997;
            // Con un corte, cada pieza dura lo que duró: solo se pide que lo que lleva esté bien (y el audio tan largo como el vídeo).
            var expected = new RecordedFileCheck.Expected(profile, size, rate, preset.AudioChannels, pieces.Count == 1 ? button : 0) { SourceScan = signal.Scan };
            var (summary, wrong) = await RecordedFileCheck.CheckAsync(locator, file, expected);
            var (v0, a0, duration) = await StartsAsync(locator.FfprobePath, file);
            total += duration;
            lines.Add($"  · {Path.GetFileName(file)}: {summary} · empieza vídeo {v0:0.000} s, audio {a0:0.000} s · dura {duration:0.00} s");
            // Una pieza cortada por un hipo o un corte lleva lo que llegó: si la red perdió paquetes de vídeo, el decodificador tira
            // los cuadros sin referencia hasta el siguiente fotograma clave y el vídeo acaba antes que el audio. Eso se cuenta, no falla.
            bool lossy = cut != Cut.None;
            foreach (var w in wrong)
            {
                if (lossy && (w.Contains("de audio decodificado para", StringComparison.Ordinal) || w.StartsWith("cadencia", StringComparison.Ordinal)))
                    lines.Add($"    (tras el {(cut == Cut.Stall ? "hipo" : "corte")}: {w})");
                else problems.Add($"{Path.GetFileName(file)}: {w}");
            }
            if (!(Math.Abs(a0 - v0) <= 0.1)) problems.Add($"{Path.GetFileName(file)}: el audio empieza {a0 - v0:+0.000;-0.000} s respecto al vídeo");
        }
        if (cut == Cut.None)
        {
            extras.Add(total - button);
            lines[0] += $" · {total:0.00} s para {button:0.00} s de botón (pre-roll y cola {total - button:0.00} s)";
            if (pieces.Count != 1) problems.Add($"{pieces.Count} piezas en una grabación sin cortes");
            if (!(total >= button + 1.0)) problems.Add($"sin pre-roll: {total:0.00} s para {button:0.00} s de botón");
            if (alarms.Contains(nameof(AlarmType.FramesDropped))) problems.Add("alarma de frames perdidos");
            if (run.InterruptionCount > interruptionsBefore) problems.Add($"grabación interrumpida: {string.Join(" | ", run.InterruptionsSince(interruptionsBefore))}");
        }
        else
        {
            // Lo perdido: lo que una grabación sin cortes lleva de más (pre-roll y cola tras Detener, la media de las anteriores) menos
            // lo que esta lleva de más.
            string lost = extras.Count > 0 ? $" · contenido perdido ≈ {Math.Max(0, extras.Average() - (total - button)):0.0} s" : "";
            lines[0] += $" · {total:0.00} s en total para {button:0.00} s de botón{lost}";
        }
        if (pieces.Count == 0) problems.Add("no se generó ningún archivo");
        run.Add(lines);
        foreach (var p in problems) { run.Add($"  ✗ {p}"); run.Problem($"{name}: {p}"); }
    }

    /// <summary>Dónde empiezan el vídeo y el audio del archivo (start_time de cada uno) y cuánto dura.</summary>
    private static async Task<(double Video, double Audio, double Duration)> StartsAsync(string ffprobe, string file)
    {
        using var doc = JsonDocument.Parse(await RecordedFileCheck.RunAsync(ffprobe, "-v", "error", "-show_entries",
            "stream=codec_type,start_time:format=duration", "-of", "json", file));
        var streams = doc.RootElement.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : new List<JsonElement>();
        double Start(string type) => streams.Where(x => RecordedFileCheck.Text(x, "codec_type") == type)
            .Select(x => RecordedFileCheck.Number(x, "start_time")).DefaultIfEmpty(double.NaN).First();
        double duration = doc.RootElement.TryGetProperty("format", out var f) ? RecordedFileCheck.Number(f, "duration") : double.NaN;
        return (Start("video"), Start("audio"), duration);
    }

    // --- El receptor: el FFmpeg hijo de esta prueba que abre la URL de la fuente (los del canal leen del relé, en 127.0.0.1) ---

    private static Process? FindReceiver(string url)
    {
        foreach (var p in Process.GetProcessesByName("ffmpeg"))
        {
            if (ParentOf(p) == Environment.ProcessId && CommandLineOf(p) is { } cmd && cmd.Contains(url, StringComparison.Ordinal)) return p;
            p.Dispose();
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int size, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);

    private static int? ParentOf(Process p)
    {
        try
        {
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? info.InheritedFromUniqueProcessId.ToInt32() : null;
        }
        catch { return null; } // terminó, o es de otro usuario
    }

    /// <summary>La línea de comandos de un proceso (ProcessCommandLineInformation, Windows 8.1+).</summary>
    private static string? CommandLineOf(Process p)
    {
        try
        {
            NtQueryInformationProcess(p.Handle, 60, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(p.Handle, 60, buffer, needed, out _) != 0) return null;
                int length = (ushort)Marshal.ReadInt16(buffer); // UNICODE_STRING: Length, MaximumLength, Buffer
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size), length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return null; }
    }

    private static void Suspend(Process p) => NtSuspendProcess(p.Handle);
    private static void Resume(Process p) => NtResumeProcess(p.Handle);

    /// <summary>Lo que se va midiendo de una fuente: registro, cuadros de preview, señal, piezas, interrupciones, el informe y lo que falla.</summary>
    private sealed class Run
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string? _secret;
        private readonly List<string> _log = new();
        private readonly List<double> _frames = new();
        private readonly List<(double At, SignalState State)> _signal = new();
        private readonly List<string> _pieces = new();
        private readonly List<string> _interruptions = new();
        private readonly List<string> _report = new();
        private readonly List<string> _events = new();

        public Run(string label, string? secret)
        {
            Label = label;
            _secret = string.IsNullOrEmpty(secret) ? null : secret;
            Dir = Path.Combine(Path.GetTempPath(), $"baioss-live-net-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Dir);
        }

        public string Label { get; }
        public string Dir { get; }
        public List<string> Problems { get; } = new();
        public double Now => _clock.Elapsed.TotalSeconds;

        /// <summary>El registro tal cual, para <see cref="RecordedFileCheck.WaitForTheEngineAsync"/> (que bloquea la lista al leerla).</summary>
        public List<string> LogsForWaiting => _log;

        private string Safe(string s) => _secret is null ? s : s.Replace(_secret, "***", StringComparison.Ordinal);

        public void Log(string line) { lock (_log) _log.Add($"{Now,8:0.000} s {Safe(line)}"); }
        public void Event(string what) { lock (_events) _events.Add($"{Now,8:0.0} s {Safe(what)}"); }
        public void Signal(SignalState state, string what) { lock (_signal) _signal.Add((Now, state)); Event(what); }
        public void Frame() { lock (_frames) _frames.Add(Now); }
        public void Piece(string file) { lock (_pieces) _pieces.Add(file); }
        public void Interrupted(string what) { lock (_interruptions) _interruptions.Add($"{Now:0.0} s {Safe(what)}"); Event("grabación interrumpida: " + what); }
        public void Problem(string what) { lock (Problems) Problems.Add(Safe(what)); }
        public void Add(string line) { lock (_report) _report.Add(Safe(line)); }
        public void Add(IEnumerable<string> lines) { foreach (var l in lines) Add(l); }

        public int PieceCount { get { lock (_pieces) return _pieces.Count; } }
        public List<string> PiecesSince(int index) { lock (_pieces) return _pieces.Skip(index).Distinct().ToList(); }
        public int InterruptionCount { get { lock (_interruptions) return _interruptions.Count; } }
        public List<string> InterruptionsSince(int index) { lock (_interruptions) return _interruptions.Skip(index).ToList(); }

        /// <summary>¿Llegan cuadros de preview con normalidad ahora (al menos la mitad de la cadencia en el último segundo)?</summary>
        public bool PreviewFlowingAt(double t) { lock (_frames) return _frames.Count(x => x > t - 1 && x <= t) >= 12; }

        /// <summary>Cuadros por segundo del preview entre dos instantes, el hueco mayor y el cuarto de segundo con menos y con más cuadros.</summary>
        public string PreviewSummary(double from, double to)
        {
            double[] f; lock (_frames) f = _frames.Where(x => x >= from && x < to).ToArray();
            if (f.Length < 2) return "sin cuadros";
            double gap = f.Zip(f.Skip(1), (a, b) => b - a).Max();
            var quarters = Enumerable.Range(0, (int)((to - from) / 0.25)).Select(k => f.Count(x => x >= from + k * 0.25 && x < from + (k + 1) * 0.25)).ToArray();
            return string.Create(CultureInfo.InvariantCulture, $"{f.Length / (to - from):0.00} fps, hueco máx {gap:0.000} s, por 250 ms {quarters.Min()}–{quarters.Max()}");
        }

        /// <summary>Alrededor de un cambio de proceso (de 1 s antes a 5 s después): el hueco mayor sin cuadros (también hasta el final de
        /// la ventana) y el cuarto de segundo con más cuadros.</summary>
        public (double Gap, int Burst) AroundTransition(double t) => Around(t - 1, t, t + 5);

        /// <summary>De <paramref name="from"/> a <paramref name="to"/>: el hueco mayor sin cuadros (contando los bordes) y el cuarto de
        /// segundo con más cuadros desde <paramref name="burstFrom"/>.</summary>
        public (double Gap, int Burst) Around(double from, double burstFrom, double to)
        {
            double[] f; lock (_frames) f = _frames.Where(x => x >= from && x <= to).ToArray();
            var edges = f.Prepend(from).Append(to).ToArray();
            double gap = edges.Zip(edges.Skip(1), (a, b) => b - a).Max();
            int windows = (int)Math.Floor((to - burstFrom) / 0.25);
            int burst = Enumerable.Range(0, Math.Max(windows, 1)).Select(k => f.Count(x => x >= burstFrom + k * 0.25 && x < burstFrom + (k + 1) * 0.25)).Max();
            return (gap, burst);
        }

        /// <summary>
        /// Qué pasó tras un hipo o un corte en <paramref name="at"/> (soltado en <paramref name="released"/>), mirando hacia atrás desde
        /// <paramref name="end"/>: lo que hizo la señal, cuándo dejó de haber cuadros, cuándo volvieron para quedarse (el principio del
        /// último tramo sin huecos mayores que los de la propia fuente, <paramref name="gapLimit"/>) y la ráfaga mayor al soltar (lo
        /// atrasado, de golpe).
        /// </summary>
        public string DescribeCut(string what, double at, double released, double end, double recordingStart, double gapLimit)
        {
            double[] f; lock (_frames) f = _frames.Where(x => x >= at - 1 && x <= end).ToArray();
            (double At, SignalState State)[] s; lock (_signal) s = _signal.Where(x => x.At >= at).ToArray();
            double lastBefore = f.LastOrDefault(x => x <= at + 0.25, at);
            // El último tramo continuo: desde el final hacia atrás mientras no haya un hueco mayor que los de la fuente.
            int i = f.Length - 1;
            while (i > 0 && f[i] - f[i - 1] <= gapLimit) i--;
            double backAt = f.Length > 0 ? f[Math.Max(i, 0)] : double.NaN;
            var (_, burst) = Around(released, released, Math.Min(end, released + 5));
            string signal = s.Length == 0 ? "la señal no cambió" : "señal " + string.Join(" → ", s.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.State} a los {x.At - at:0.0} s")));
            return string.Create(CultureInfo.InvariantCulture,
                $"{what} a los {at - recordingStart:0.0} s de grabar: {signal}; preview sin cuadros desde los {lastBefore - at:0.0} s, " +
                $"de vuelta para quedarse a los {backAt - at:0.0} s ({backAt - released:0.0} s tras soltarlo), ráfaga al soltar {burst}/250 ms");
        }

        /// <summary>El códec de vídeo de la última grabación, según la línea de comandos que registró el motor.</summary>
        public string LastEncoder()
        {
            lock (_log)
                return _log.Where(l => l.Contains("grabación", StringComparison.Ordinal) && l.Contains("-c:v ", StringComparison.Ordinal))
                           .Select(l => Regex.Match(l, @"-c:v (\S+)").Groups[1].Value).LastOrDefault() ?? "—";
        }

        /// <summary>El informe de la fuente: lo medido, los sucesos (señal, alarmas, cortes) y los avisos y errores del registro.</summary>
        public List<string> Lines()
        {
            var lines = new List<string>();
            lock (_report) lines.AddRange(_report);
            lock (_events) if (_events.Count > 0) { lines.Add("- Sucesos:"); lines.AddRange(_events.Select(e => "  · " + e)); }
            lock (_log)
            {
                var notable = _log.Where(l => l.Contains("[Warning]", StringComparison.Ordinal) || l.Contains("[Error]", StringComparison.Ordinal)
                                              || (l.Contains("[Information]", StringComparison.Ordinal) && !l.Contains("Pipeline canal", StringComparison.Ordinal))).ToList();
                if (notable.Count > 0) { lines.Add("- Registro (información, avisos y errores):"); lines.AddRange(notable.Select(l => "  · " + l)); }
            }
            return lines;
        }

        public void Cleanup()
        {
            if (Env("BAIOSS_LIVE_NET_KEEP") == "1") { Add($"  archivos en {Dir}"); return; }
            for (int i = 0; i < 20; i++)
            {
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); return; }
                catch { Thread.Sleep(300); } // el remux a faststart o la verificación pueden tener aún un archivo abierto
            }
        }
    }
}
