using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Presets;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Pruebas con una tarjeta DeckLink DE VERDAD: el mismo cableado que la aplicación (receptor permanente + relé en crudo,
/// motor unificado, cola de escritura, fMP4) contra la señal SDI real. Lo que los tests sintéticos no pueden cubrir: el
/// driver, la autodetección del modo, el audio embebido de la tarjeta, la exclusividad del dispositivo y la cadencia real.
/// Se omiten si no se define <c>BAIOSS_DECKLINK</c>:
/// <list type="bullet">
///   <item><c>BAIOSS_DECKLINK</c>: entrada CON señal («DeckLink Duo (2)»).</item>
///   <item><c>BAIOSS_DECKLINK_2</c>: otra entrada con señal (dos canales a la vez).</item>
///   <item><c>BAIOSS_DECKLINK_NOSIGNAL</c>: entrada sin señal (espera y Grabar rechazado).</item>
///   <item><c>BAIOSS_HW_OUT</c>: carpeta donde CONSERVAR las grabaciones (si no, se borran).</item>
///   <item><c>BAIOSS_HW_LONG_MINUTES</c>: minutos de la grabación larga (deriva A/V); sin ella, se omite.</item>
/// </list>
/// Las de NVENC se omiten además si este FFmpeg no abre NVENC en el equipo (sin GPU NVIDIA, o con un SDK de NVENC más nuevo
/// que el driver).
/// </summary>
public sealed class DecklinkHardwareTests
{
    private static readonly string? Device = Env("BAIOSS_DECKLINK");
    private static readonly string? Device2 = Env("BAIOSS_DECKLINK_2");
    private static readonly string? NoSignalDevice = Env("BAIOSS_DECKLINK_NOSIGNAL");
    private static readonly string? KeepDir = Env("BAIOSS_HW_OUT");
    /// <summary>Contenedor MP4/MOV como el de la build publicada: <c>Recording:FragmentedMp4</c> del appsettings embebido
    /// (hoy false, MP4 estándar). BAIOSS_HW_FMP4=1 prueba el fMP4 fragmentado.</summary>
    private static readonly bool ShippedFragmentedMp4 = Env("BAIOSS_HW_FMP4") == "1";

    private readonly ITestOutputHelper _out;
    public DecklinkHardwareTests(ITestOutputHelper output) => _out = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static FfmpegLocator Locator() => new(TestAssets.FfmpegDir!);

    private static string NewOutputDir(string name)
    {
        string root = KeepDir ?? Path.Combine(Path.GetTempPath(), "baioss-hw");
        string dir = Path.Combine(root, $"{DateTime.Now:HHmmss}-{name}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        if (KeepDir is not null) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static EncodingPreset Preset(string name) =>
        PresetCatalog.CreateBuiltIns().SingleOrDefault(p => p.Name == name) ?? throw new ArgumentException($"No existe el preset «{name}».");

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Esperando: " + what);
            await Task.Delay(100);
        }
    }

    private static int FfmpegProcessCount() => Process.GetProcessesByName("ffmpeg").Length;

    /// <summary>Un canal como lo compone la aplicación: fuente DeckLink con receptor permanente + motor unificado.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Action<string> _log;
        public required DecklinkCaptureSource Source { get; init; }
        public required FfmpegChannelEngine Engine { get; init; }
        public required Stopwatch Clock { get; init; }
        public RawCaptureReceiver Receiver => (RawCaptureReceiver)Source.Receiver!;
        public readonly List<double> Frames = new();
        public readonly List<(double At, AlarmType Type, bool Active)> Alarms = new();
        public readonly List<(double At, SignalState State, string? Label)> Signals = new();
        public readonly List<string> Unverified = new();
        public readonly List<(int Code, string Reason)> Interruptions = new();
        public readonly List<Segment> Segments = new();
        public long Overruns;
        public long RelayDrops;
        public readonly List<(double At, long Count)> RelayDropLog = new();
        public IReadOnlyList<double>? LastPeaks;

        /// <summary>Descartes del relé entre dos instantes: los de un Grabar/Detener en curso son del proceso de preview que
        /// entra o del que sale (la aplicación solo alarma mientras graba, igual que esto).</summary>
        public long RelayDropsBetween(double from, double to) { lock (RelayDropLog) return RelayDropLog.Where(d => d.At >= from && d.At <= to).Sum(d => d.Count); }

        private Rig(Action<string> log) => _log = log;

        public static Task<Rig> OpenAsync(string device, string outputRoot, RecordingProfile baseProfile, string key,
            Action<string> log, params (string Key, string Value)[] parameters)
            => OpenAsync(device, outputRoot, baseProfile, key, log, ShippedFragmentedMp4, parameters);

        public static async Task<Rig> OpenAsync(string device, string outputRoot, RecordingProfile baseProfile, string key,
            Action<string> log, bool fragmentedMp4, params (string Key, string Value)[] parameters)
        {
            var clock = Stopwatch.StartNew();
            var loggers = new DecklinkRelayContinuityTests.CaptureLogger(s => log($"{clock.Elapsed.TotalSeconds,7:0.00} s [{key}]{s}"));
            var locator = Locator();
            var definition = new InputSource { Name = $"DeckLink — {device}", Type = InputType.DecklinkSdi, Uri = device };
            foreach (var (k, v) in parameters) definition.Parameters[k] = v;
            var source = (DecklinkCaptureSource)new DecklinkCaptureSourceFactory(locator, loggers, relay: true).Create(definition);
            // Como la aplicación: cola de escritura de 10 s si el FFmpeg la admite, y fMP4 robusto.
            var queue = await locator.SupportsOutputThreadQueueAsync() ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
            var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine"))
            {
                OutputRoot = outputRoot, WriteQueue = queue, FragmentedMp4 = fragmentedMp4,
            };
            var rig = new Rig(log) { Source = source, Engine = engine, Clock = clock };
            source.SignalChanged += (_, s) => { lock (rig.Signals) rig.Signals.Add((clock.Elapsed.TotalSeconds, s.State, s.FormatLabel)); };
            source.InputDataDropped += (_, n) =>
            {
                Interlocked.Add(ref rig.RelayDrops, n);
                lock (rig.RelayDropLog) rig.RelayDropLog.Add((clock.Elapsed.TotalSeconds, n));
            };
            engine.FrameReady += (_, _) => { lock (rig.Frames) rig.Frames.Add(clock.Elapsed.TotalSeconds); };
            engine.AlarmChanged += (_, a) => { lock (rig.Alarms) rig.Alarms.Add((clock.Elapsed.TotalSeconds, a.Type, a.Active)); };
            engine.FileUnverified += (_, f) => { lock (rig.Unverified) rig.Unverified.Add($"{f.FilePath} ({f.SizeBytes} bytes)"); };
            engine.RecordingInterrupted += (_, i) => { lock (rig.Interruptions) rig.Interruptions.Add(i); };
            engine.SegmentClosed += (_, s) => { lock (rig.Segments) rig.Segments.Add(s); };
            engine.InputOverrun += (_, n) => Interlocked.Add(ref rig.Overruns, n);
            engine.AudioPeaksUpdated += (_, p) => rig.LastPeaks = p;
            await source.OpenAsync();
            await engine.StartPreviewAsync(source, baseProfile, key);
            return rig;
        }

        public int FramesBetween(double from, double to) { lock (Frames) return Frames.Count(f => f >= from && f < to); }

        /// <summary>Huecos del preview (s) entre dos instantes: el mayor intervalo sin frame.</summary>
        public double LongestPreviewGap(double from, double to)
        {
            double[] f; lock (Frames) f = Frames.Where(x => x >= from && x <= to).ToArray();
            double max = 0;
            for (int i = 1; i < f.Length; i++) max = Math.Max(max, f[i] - f[i - 1]);
            return max;
        }

        public bool AlarmRaised(AlarmType type) { lock (Alarms) return Alarms.Any(a => a.Type == type && a.Active); }

        /// <summary>¿Estuvo activa la alarma en algún momento entre dos instantes (ya lo estaba, o saltó entre medias)?</summary>
        public bool AlarmActiveBetween(AlarmType type, double from, double to)
        {
            lock (Alarms)
            {
                var events = Alarms.Where(a => a.Type == type).OrderBy(a => a.At).ToList();
                bool activeAtStart = events.Where(a => a.At <= from).Select(a => a.Active).LastOrDefault();
                return activeAtStart || events.Any(a => a.Active && a.At > from && a.At <= to);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Engine.DisposeAsync(); } catch (Exception ex) { _log("Dispose motor: " + ex.Message); }
            try { await Source.DisposeAsync(); } catch (Exception ex) { _log("Dispose fuente: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // ffprobe: lo que de verdad quedó en el archivo
    // ------------------------------------------------------------------------------------------------------------------

    private sealed record StreamInfo(string Type, string Codec, string? Encoder, string? FieldOrder, string? Title,
        int Width, int Height, int Channels, double StartTime, double Duration, double AvgFps, long Packets, string? Profile = null);

    private sealed record MediaInfo(double Duration, IReadOnlyList<StreamInfo> Streams, string Raw)
    {
        public StreamInfo? Video => Streams.FirstOrDefault(s => s.Type == "video");
        public IEnumerable<StreamInfo> Audio => Streams.Where(s => s.Type == "audio");
    }

    private static async Task<(string Out, string Err, int Exit)> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await o, await e, p.ExitCode);
    }

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return double.NaN;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s)
        {
            if (s.Contains('/'))
            {
                var parts = s.Split('/');
                return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
                       double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0 ? n / d : double.NaN;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN;
        }
        return double.NaN;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<MediaInfo> ProbeAsync(string file)
    {
        var (json, err, exit) = await RunAsync(Locator().FfprobePath, "-v", "error", "-count_packets", "-of", "json",
            "-show_entries", "format=duration:stream=codec_type,codec_name,profile,field_order,width,height,channels,start_time,duration,avg_frame_rate,nb_read_packets:stream_tags=encoder,title,handler_name",
            file);
        Assert.True(exit == 0, $"ffprobe falló con {Path.GetFileName(file)}: {err}");
        using var doc = JsonDocument.Parse(json);
        var streams = new List<StreamInfo>();
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            JsonElement tags = s.TryGetProperty("tags", out var t) ? t : default;
            string? Tag(string n) => tags.ValueKind == JsonValueKind.Object ? Str(tags, n) : null;
            streams.Add(new StreamInfo(Str(s, "codec_type") ?? "", Str(s, "codec_name") ?? "", Tag("encoder"), Str(s, "field_order"),
                Tag("title") ?? Tag("handler_name"), (int)(Num(s, "width") is var w && !double.IsNaN(w) ? w : 0),
                (int)(Num(s, "height") is var h && !double.IsNaN(h) ? h : 0), (int)(Num(s, "channels") is var c && !double.IsNaN(c) ? c : 0),
                Num(s, "start_time"), Num(s, "duration"), Num(s, "avg_frame_rate"), (long)(Num(s, "nb_read_packets") is var n && !double.IsNaN(n) ? n : 0),
                Str(s, "profile")));
        }
        double duration = doc.RootElement.TryGetProperty("format", out var f) ? Num(f, "duration") : double.NaN;
        return new MediaInfo(duration, streams, json);
    }

    /// <summary>Marcas de tiempo (s) de los paquetes de vídeo, ordenadas: para buscar huecos en la grabación.</summary>
    private static async Task<double[]> VideoPtsAsync(string file)
    {
        var (csv, _, _) = await RunAsync(Locator().FfprobePath, "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", file);
        return csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => double.TryParse(l.TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
    }

    /// <summary>Cuadros que <c>idet</c> ve entrelazados (TFF+BFF), progresivos e indeterminados en los primeros segundos del
    /// archivo. La detección por cuadro, no la «multi»: esa arrastra estado y en escenas oscuras y ruidosas da falsos positivos al
    /// inicio. idet solo clasifica lo que se mueve: un cuadro igual al anterior y al siguiente queda indeterminado.</summary>
    private static async Task<(int Interlaced, int Progressive, int Undetermined)> DetectInterlaceAsync(string file)
    {
        var (_, err, _) = await RunAsync(Locator().FfmpegPath, "-hide_banner", "-nostats", "-i", file, "-t", "8", "-map", "0:v:0", "-vf", "idet", "-f", "null", "-");
        var line = err.Split('\n').LastOrDefault(l => l.Contains("Single frame detection", StringComparison.Ordinal)) ?? "";
        int Count(string key)
        {
            int i = line.IndexOf(key + ":", StringComparison.Ordinal);
            if (i < 0) return 0;
            var rest = line[(i + key.Length + 1)..].TrimStart();
            return int.Parse(new string(rest.TakeWhile(char.IsDigit).ToArray()), CultureInfo.InvariantCulture);
        }
        return (Count("TFF") + Count("BFF"), Count("Progressive"), Count("Undetermined"));
    }

    /// <summary>Marcas de entrelazado y de campo superior primero del primer cuadro DECODIFICADO (las del códec, no las del
    /// contenedor).</summary>
    private static async Task<(bool Interlaced, bool TopFieldFirst)> FirstFrameScanAsync(string file)
    {
        var (csv, _, _) = await RunAsync(Locator().FfprobePath, "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1",
            "-show_entries", "frame=interlaced_frame,top_field_first", "-of", "csv=p=0", file);
        var fields = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.Split(',') ?? Array.Empty<string>();
        return (fields.ElementAtOrDefault(0) == "1", fields.ElementAtOrDefault(1) == "1");
    }

    /// <summary>Pico máximo (dBFS) de una pista de audio del archivo, decodificándola.</summary>
    private static async Task<double> MaxVolumeAsync(string file, int audioIndex)
    {
        var (_, err, _) = await RunAsync(Locator().FfmpegPath, "-hide_banner", "-nostats", "-i", file, "-map", $"0:a:{audioIndex}", "-af", "volumedetect", "-f", "null", "-");
        var line = err.Split('\n').LastOrDefault(l => l.Contains("max_volume:", StringComparison.Ordinal));
        if (line is null) return double.NegativeInfinity;
        var v = line[(line.IndexOf("max_volume:", StringComparison.Ordinal) + 11)..].Replace("dB", "").Trim();
        return v.StartsWith("-inf", StringComparison.Ordinal) ? double.NegativeInfinity
            : double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string Describe(MediaInfo m) => string.Join(" | ", m.Streams.Select(s =>
        s.Type == "video"
            ? $"v:{s.Codec}[{s.Encoder}] {s.Width}x{s.Height} {s.FieldOrder} {s.AvgFps:0.###}fps start {s.StartTime:0.###} dur {s.Duration:0.###} pk {s.Packets}"
            : $"a:{s.Codec} {s.Channels}ch «{s.Title}» start {s.StartTime:0.###} dur {s.Duration:0.###}")) + $" | format {m.Duration:0.###} s";

    /// <summary>Comprueba que un archivo de grabación es continuo y reproducible: duración, cadencia, sin huecos, audio alineado.</summary>
    private async Task<MediaInfo> AssertContinuousRecordingAsync(string file, double expectedSeconds, double expectedFps,
        double tolerance, string? expectCodec, string? expectEncoder = null)
    {
        Assert.True(File.Exists(file), $"No existe {file}");
        var m = await ProbeAsync(file);
        _out.WriteLine($"{Path.GetFileName(file)} ({new FileInfo(file).Length / 1_000_000.0:0.0} MB): {Describe(m)}");
        var v = m.Video;
        Assert.True(v is not null, "El archivo no tiene vídeo: " + m.Raw);
        if (expectCodec is not null) Assert.Equal(expectCodec, v!.Codec);
        if (expectEncoder is not null)
            Assert.True(v!.Encoder?.Contains(expectEncoder, StringComparison.Ordinal) == true, $"Encoder «{v.Encoder}», se esperaba «{expectEncoder}».");
        double duration = double.IsNaN(v!.Duration) ? m.Duration : v.Duration;
        Assert.True(Math.Abs(duration - expectedSeconds) <= tolerance,
            $"Duración del vídeo {duration:0.00} s para {expectedSeconds:0.0} s de grabación (±{tolerance:0.0}).");

        // Cadencia: paquetes de vídeo por segundo de duración ≈ la tasa de salida (sin frames perdidos ni duplicados de más).
        double fps = v.Packets / duration;
        Assert.True(Math.Abs(fps - expectedFps) / expectedFps < 0.02, $"Cadencia real {fps:0.000} fps ({v.Packets} paquetes en {duration:0.00} s), se esperaban {expectedFps:0.000}.");

        // Sin huecos: ningún salto entre marcas de tiempo de más de 2,5 periodos de frame.
        var pts = await VideoPtsAsync(file);
        double maxGap = 0; double at = 0;
        for (int i = 1; i < pts.Length; i++) if (pts[i] - pts[i - 1] > maxGap) { maxGap = pts[i] - pts[i - 1]; at = pts[i - 1]; }
        Assert.True(maxGap <= 2.5 / expectedFps, $"Hueco de {maxGap * 1000:0} ms en el vídeo @{at:0.00} s.");

        // Audio presente, con nivel (el par elegido tiene sonido en la señal de prueba) y alineado con el vídeo.
        var a = m.Audio.FirstOrDefault();
        Assert.True(a is not null, "El archivo no tiene audio: " + m.Raw);
        if (!double.IsNaN(a!.StartTime) && !double.IsNaN(v.StartTime))
            Assert.True(Math.Abs(a.StartTime - v.StartTime) < 0.1, $"Audio y vídeo empiezan desalineados: audio {a.StartTime:0.000} s, vídeo {v.StartTime:0.000} s.");
        if (!double.IsNaN(a.Duration))
            Assert.True(Math.Abs(a.Duration - duration) < 0.25, $"Audio {a.Duration:0.000} s frente a vídeo {duration:0.000} s.");
        return m;
    }

    private void Dump(string title, IEnumerable<string> lines)
    {
        _out.WriteLine($"--- {title} ---");
        foreach (var l in lines) _out.WriteLine(l);
    }

    /// <summary>Argumentos del último proceso de grabación de un canal, tal como los registró el motor.</summary>
    private static string LastRecordingPipeline(List<string> log, string key)
    {
        lock (log) return log.LastOrDefault(l => l.Contains($"Pipeline canal {key} (grabación+preview)", StringComparison.Ordinal)) ?? "";
    }

    // ------------------------------------------------------------------------------------------------------------------
    // NVENC: si abre, carga de CPU/GPU y sesiones
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>Omite la prueba si este FFmpeg no abre el codificador en este equipo: sin GPU NVIDIA, o con un SDK de NVENC
    /// más nuevo que el driver (el FFmpeg empaquetado, SDK 13.1, no abre en una Quadro P400 con el driver 582).</summary>
    private static async Task SkipUnlessEncoderOpensAsync(string encoder)
    {
        var (ok, detail) = await Locator().ProbeVideoEncoderAsync(encoder);
        Skip.IfNot(ok, $"{encoder} no abre con este FFmpeg en este equipo: {detail}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>CPU media (núcleos lógicos) entre <see cref="Start"/> y <see cref="Stop"/>: la de los ffmpeg del FFmpeg bajo
    /// prueba (por ruta: no cuentan los de otros programas), la del testhost (receptores y relés DeckLink) y la del sistema.</summary>
    private sealed class CpuSampler
    {
        private readonly string _ffmpegPath;
        private readonly Stopwatch _wall = new();
        private Dictionary<int, TimeSpan> _ffmpeg = new();
        private TimeSpan _self;
        private long _idle, _kernel, _user;

        public CpuSampler(string ffmpegPath) => _ffmpegPath = Path.GetFullPath(ffmpegPath);

        private Dictionary<int, TimeSpan> FfmpegTimes()
        {
            var times = new Dictionary<int, TimeSpan>();
            foreach (var p in Process.GetProcessesByName("ffmpeg"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, _ffmpegPath, StringComparison.OrdinalIgnoreCase))
                        times[p.Id] = p.TotalProcessorTime;
                }
                catch { /* salió entre medias, o sin acceso */ }
                finally { p.Dispose(); }
            }
            return times;
        }

        public void Start()
        {
            _ffmpeg = FfmpegTimes();
            using (var me = Process.GetCurrentProcess()) _self = me.TotalProcessorTime;
            GetSystemTimes(out _idle, out _kernel, out _user);
            _wall.Restart();
        }

        /// <summary>Núcleos ocupados de media; de los ffmpeg, solo los que vivieron todo el intervalo (y cuántos eran).</summary>
        public (double Ffmpeg, double Testhost, double System, int Processes) Stop()
        {
            double wall = _wall.Elapsed.TotalSeconds;
            double ffmpeg = 0; int processes = 0;
            foreach (var (pid, time) in FfmpegTimes())
                if (_ffmpeg.TryGetValue(pid, out var before)) { ffmpeg += (time - before).TotalSeconds; processes++; }
            double self;
            using (var me = Process.GetCurrentProcess()) self = (me.TotalProcessorTime - _self).TotalSeconds;
            GetSystemTimes(out long idle, out long kernel, out long user);
            double total = (kernel - _kernel) + (user - _user); // el tiempo de kernel incluye el ocioso
            double system = total > 0 ? (total - (idle - _idle)) / total * Environment.ProcessorCount : double.NaN;
            return (ffmpeg / wall, self / wall, system, processes);
        }
    }

    /// <summary>«uso de la GPU %, uso del codificador %, sesiones NVENC, fps medios, MiB usados», según nvidia-smi.</summary>
    private static async Task<string> NvidiaSmiAsync()
    {
        try
        {
            var (o, _, _) = await RunAsync("nvidia-smi",
                "--query-gpu=utilization.gpu,utilization.encoder,encoder.stats.sessionCount,encoder.stats.averageFps,memory.used",
                "--format=csv,noheader,nounits");
            return o.Trim();
        }
        catch (Exception ex) { return "nvidia-smi: " + ex.Message; }
    }

    /// <summary>Un proceso que ocupa una sesión NVENC: codifica gris a tiempo real (sin cargar la GPU) durante 15 min como
    /// mucho, por si la prueba muere sin matarlo. Si la GPU no tiene sesiones libres, sale enseguida con el error de NVENC.</summary>
    private static Process StartDummyNvencSession()
    {
        var psi = new ProcessStartInfo(Locator().FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "color=c=gray:s=640x360:r=25",
                     "-t", "900", "-c:v", "h264_nvenc", "-f", "null", "-" })
            psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    private static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ya salió */ }
        p.Dispose();
    }

    // ==================================================================================================================

    [SkippableFact]
    public async Task Preview_LocksOnTheRealSignal_AndPaintsAtTheSourceCadence()
    {
        Skip.If(Device is null || TestAssets.FfmpegDir is null, "Sin DeckLink configurada (BAIOSS_DECKLINK).");
        var log = new List<string>();
        var dir = NewOutputDir("preview");
        int before = FfmpegProcessCount();
        try
        {
            var rig = await Rig.OpenAsync(Device!, dir, Preset("H.264 Broadcast · 1080i59.94 MP4").ToProfile(), "A", l => { lock (log) log.Add(l); },
                (AudioSelection.ChannelsKey, "auto"));
            await using (rig)
            {
                await WaitUntilAsync(() => rig.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
                var signal = rig.Source.CurrentSignal;
                _out.WriteLine($"Señal: {signal.FormatLabel} · {signal.Resolution} · {signal.FrameRate?.Value:0.###} fps · audio {signal.AudioChannels} canales");
                await WaitUntilAsync(() => rig.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en marcha");
                await Task.Delay(2000);
                double t0 = rig.Clock.Elapsed.TotalSeconds;
                await Task.Delay(10_000);
                double t1 = rig.Clock.Elapsed.TotalSeconds;
                int frames = rig.FramesBetween(t0, t1);
                double fps = frames / (t1 - t0);
                double gap = rig.LongestPreviewGap(t0, t1);
                _out.WriteLine($"Preview: {frames} frames en {t1 - t0:0.0} s = {fps:0.00} fps; hueco máximo {gap * 1000:0} ms; medidores: {(rig.LastPeaks is { } p ? string.Join(" ", p.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture))) : "—")}");

                Assert.Equal(new Resolution(1920, 1080), signal.Resolution);
                Assert.NotNull(signal.FrameRate);
                double sourceFps = signal.FrameRate!.Value.Value;
                Assert.InRange(fps, sourceFps * 0.97, sourceFps * 1.03);
                Assert.True(gap < 0.25, $"El preview se detuvo {gap * 1000:0} ms.");
                // «auto» abre la tarjeta con 16 canales (la Duo 2 los admite) y los medidores reciben uno por canal.
                Assert.Equal(16, signal.AudioChannels);
                Assert.Equal(16, rig.LastPeaks?.Count);
                Assert.True(rig.LastPeaks![0] > -50, $"Sin nivel en el canal 1 ({rig.LastPeaks[0]:0.0} dBFS).");
                Assert.Equal(0, rig.Receiver.Restarts);
                Assert.Equal(0, Interlocked.Read(ref rig.RelayDrops));
            }
            await Task.Delay(1500);
            Assert.True(FfmpegProcessCount() <= before, "Quedaron procesos FFmpeg vivos tras cerrar el canal.");
        }
        finally { Dump("registro", log); Cleanup(dir); }
    }

    /// <summary>Grabar ~15 s con cada preset de fábrica relevante para una señal 1080i59.94 y comprobar el archivo.</summary>
    [SkippableTheory]
    [InlineData("H.264 Broadcast · 1080i59.94 MP4", "h264", "libx264", false)]
    [InlineData("H.264 Broadcast · 1080i59.94 TS", "h264", null, false)]
    // Todos los NVENC de fábrica. Si este FFmpeg abre NVENC aquí, el archivo tiene que ser de NVENC de verdad (no una
    // degradación silenciosa) y con sus argumentos; si no, el canal degrada solo y la alarma EncoderFallback lo dice.
    [InlineData("H.264 NVENC · HD 720p50", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · HD 720p59.94", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · FullHD 1080p23.98", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · FullHD 1080p25", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · FullHD 1080p29.97", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · FullHD 1080p50", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · FullHD 1080p59.94", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · FullHD 1080p60", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · 4K UHD 2160p50", "h264", "h264_nvenc", null)]
    [InlineData("H.264 NVENC · 4K UHD 2160p59.94", "h264", "h264_nvenc", null)]
    [InlineData("HEVC NVENC · FullHD 1080p25", "hevc", "hevc_nvenc", null)]
    [InlineData("HEVC NVENC · FullHD 1080p29.97", "hevc", "hevc_nvenc", null)]
    [InlineData("HEVC NVENC · FullHD 1080p50", "hevc", "hevc_nvenc", null)]
    [InlineData("HEVC NVENC · FullHD 1080p59.94", "hevc", "hevc_nvenc", null)]
    [InlineData("HEVC NVENC · 4K UHD 2160p50", "hevc", "hevc_nvenc", null)]
    [InlineData("HEVC NVENC · 4K UHD 2160p59.94", "hevc", "hevc_nvenc", null)]
    [InlineData("H.264 Broadcast · FullHD 1080p25", "h264", "libx264", false)]
    [InlineData("XDCAM HD422 · 1080i59.94 50 Mbps", "mpeg2video", null, false)]
    [InlineData("MPEG-2 TS · HD 1080i59.94", "mpeg2video", null, false)]
    // ProRes con prores_aw: con prores_ks ninguno llegaba a tiempo real en el i7-8700 (149–302 cuadros perdidos en 15 s).
    [InlineData("ProRes 422 HQ · 1080i59.94", "prores", "prores_aw", false)]
    [InlineData("ProRes 422 Proxy · 1080p25", "prores", "prores_aw", false)]   // con prores_ks, el más lento (0,76×)
    [InlineData("ProRes 4444 XQ · 1080p25", "prores", "prores_aw", false)]     // el más pesado en 1080 (4:4:4)
    [InlineData("ProRes 422 HQ · 1080p59.94", "prores", "prores_aw", false)]   // un cuadro por campo: el doble de cuadros
    [InlineData("DNxHD 220 · 1080i59.94 (MOV)", "dnxhd", "dnxhd", false)]
    [InlineData("MXF OP1A · DNxHD 220 1080i59.94", "dnxhd", null, false)]
    public Task Recording_WithFactoryPreset_IsContinuousAndPlayable(string presetName, string codec, string? encoder, bool? expectFallback)
        => RecordAndVerifyAsync(presetName, Preset(presetName).ToProfile(), codec, encoder, expectFallback);

    /// <summary>
    /// Un canal que aplicó uno de los antiguos presets «DNxHR HQ · 1080i…» guarda su copia del perfil: DNxHR entrelazado, que
    /// FFmpeg no sabe entrelazar (grababa los campos en cuadros progresivos). El motor lo graba como su DNxHD clásico (220 a
    /// 29.97) con los dos campos de verdad, sin que nadie vuelva a aplicar el preset.
    /// </summary>
    [SkippableFact]
    public Task Recording_WithALegacyInterlacedDnxHrProfile_RecordsInterlacedDnxhd()
    {
        var legacy = new EncodingPreset
        {
            Name = "DNxHR HQ · 1080i59.94 (MOV)", Container = ContainerFormat.Mov, VideoCodec = VideoCodec.DnxHr,
            Width = 1920, Height = 1080, FrameRateNum = 30000, FrameRateDen = 1001, GopSize = 1, PixelFormat = PixelFormat.Yuv422p,
            EncoderProfile = EncoderProfile.DnxHrHq, ScanType = ScanType.InterlacedTff, AudioCodec = AudioCodec.Pcm,
        };
        return RecordAndVerifyAsync(legacy.Name, legacy.ToProfile(), "dnxhd", "dnxhd", false);
    }

    private async Task RecordAndVerifyAsync(string presetName, RecordingProfile profile, string codec, string? encoder, bool? expectFallback)
    {
        Skip.If(Device is null || TestAssets.FfmpegDir is null, "Sin DeckLink configurada (BAIOSS_DECKLINK).");
        var log = new List<string>();
        var dir = NewOutputDir("rec-" + string.Concat(presetName.Where(char.IsLetterOrDigit)));
        // NVENC: si la GPU no puede (driver/sesiones), el canal degrada solo y la alarma EncoderFallback lo dice.
        bool nvenc = FfmpegCodecMap.IsGpuEncoder(profile.VideoCodec);
        bool nvencUsable = nvenc && (await Locator().ProbeVideoEncoderAsync(FfmpegCodecMap.VideoEncoder(profile.VideoCodec))).Ok;
        bool fallback = expectFallback ?? (nvenc && !nvencUsable);
        try
        {
            var rig = await Rig.OpenAsync(Device!, dir, profile, "A", l => { lock (log) log.Add(l); });
            await using (rig)
            {
                await WaitUntilAsync(() => rig.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
                await WaitUntilAsync(() => rig.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en marcha");
                double sourceFps = rig.Source.CurrentSignal.FrameRate?.Value ?? 29.97;

                double tStart = rig.Clock.Elapsed.TotalSeconds;
                await rig.Engine.StartRecordingAsync(Guid.NewGuid(), profile);
                double tStarted = rig.Clock.Elapsed.TotalSeconds;
                await Task.Delay(15_000);
                double tStop = rig.Clock.Elapsed.TotalSeconds;
                await rig.Engine.StopRecordingAsync();
                double tStopped = rig.Clock.Elapsed.TotalSeconds;
                await Task.Delay(1500); // verificación + remux faststart en segundo plano

                _out.WriteLine($"{presetName}: Grabar {tStarted - tStart:0.00} s · Detener {tStopped - tStop:0.00} s · NVENC usable: {nvencUsable} · " +
                               $"desbordes de entrada {rig.Overruns} · descartes del relé {rig.RelayDrops} · reaperturas de la tarjeta {rig.Receiver.Restarts}");
                lock (rig.Alarms) foreach (var a in rig.Alarms) _out.WriteLine($"  alarma @{a.At:0.0} s {a.Type} {(a.Active ? "ON" : "off")}");

                // Una sola pieza, sin restos de intentos fallidos (p. ej. el del codificador que no abrió).
                var files = Directory.GetFiles(dir);
                _out.WriteLine("Archivos: " + string.Join(", ", files.Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} B)")));
                Assert.True(files.Length == 1, "Se esperaba UN archivo: " + string.Join(", ", files.Select(Path.GetFileName)));
                Assert.Empty(rig.Unverified);
                Assert.False(rig.AlarmRaised(AlarmType.RecordingUnverified), "Alarma de grabación sin verificar.");
                Assert.Equal(fallback, rig.AlarmRaised(AlarmType.EncoderFallback));
                Assert.Empty(rig.Interruptions);
                Assert.Equal(0, rig.Receiver.Restarts);

                double expectedFps = profile.OutputFrameRate?.Value ?? sourceFps;
                // El motor sigue grabando hasta que el proceso de preview nuevo toma el relevo (así el preview no se congela al
                // detener): el archivo acaba entre el Detener y el final de ese relevo, que con un preset pesado tarda más
                // (ProRes HQ 1080p59.94: 2,1 s medidos). Con fallback se pierde el arranque del intento que no abrió: más margen.
                double handover = Math.Max(0, tStopped - tStop - 0.6);
                double expectedSeconds = tStop - tStarted + 0.3 + handover / 2;
                // Degradado, el archivo es del codificador de la cadena que abrió, no del pedido.
                var media = await AssertContinuousRecordingAsync(files[0], expectedSeconds, expectedFps, (fallback ? 2.5 : 1.5) + handover / 2,
                    codec, fallback ? null : encoder);
                bool sourceInterlaced = rig.Source.CurrentSignal.Scan is ScanType.InterlacedTff or ScanType.InterlacedBff;
                var (combed, progressive, undetermined) = await DetectInterlaceAsync(files[0]);
                var coded = await FirstFrameScanAsync(files[0]);
                _out.WriteLine($"Barrido: señal {rig.Source.CurrentSignal.Scan}, preset {profile.ScanType}, contenedor «{media.Video!.FieldOrder}», " +
                               $"perfil «{media.Video.Profile}», cuadro codificado {(coded.Interlaced ? (coded.TopFieldFirst ? "entrelazado TFF" : "entrelazado BFF") : "progresivo")}, " +
                               $"idet {combed} entrelazados / {progressive} progresivos / {undetermined} indeterminados");
                if (profile.ScanType is ScanType.InterlacedTff)
                {
                    // Entrelazado de verdad: el CÓDEC marca los cuadros entrelazados TFF (no solo el contenedor). El MOV de FFmpeg
                    // declara el entrelazado de DNxHD en el átomo ARES de Avid, no en «fiel»: ahí ffprobe dice «unknown».
                    Assert.True(coded.Interlaced, "Preset entrelazado, pero el códec escribió cuadros progresivos.");
                    Assert.True(coded.TopFieldFirst, "Preset TFF, pero el códec marcó los cuadros como BFF.");
                    bool dnxhdInMov = media.Video.Codec == "dnxhd" && profile.Container is ContainerFormat.Mov;
                    Assert.True(media.Video.FieldOrder is "tt" or "tb" || (dnxhdInMov && media.Video.FieldOrder is null or "unknown"),
                        $"El contenedor declara «{media.Video.FieldOrder}», no entrelazado TFF.");
                    // DNxHD clásico (CID entrelazado), no DNxHR con los campos guardados en cuadros progresivos.
                    if (media.Video.Codec == "dnxhd") Assert.Equal("DNXHD", media.Video.Profile);
                }
                else if (profile.ScanType is ScanType.Progressive && sourceInterlaced)
                {
                    // Preset progresivo con señal 1080i: desentrelazado, sin peine (de lo que idet clasifica, menos del 10 %
                    // entrelazado). Solo se mide si la imagen se mueve: con la carta casi fija de la emisora fuera de emisión, idet
                    // deja indeterminados más de 7 de cada 10 cuadros, y de los pocos que clasifica llegó a dar un 28 % «entrelazados»
                    // con bwdif en marcha (ruido: por la misma cadena, un 1080i sintético en movimiento sale 121 de 121 progresivo).
                    int classified = combed + progressive;
                    if (classified >= 30 && classified >= undetermined)
                        Assert.True(combed < classified / 10.0, $"Peine en un preset progresivo: idet ve {combed} cuadros entrelazados y {progressive} progresivos.");
                    else _out.WriteLine($"idet solo clasifica {classified} de {classified + undetermined} cuadros (imagen casi fija): el peine no se puede medir con esta señal.");
                    // Con cualquier imagen: la rama de grabación desentrelaza con bwdif, con la paridad de la señal y, si el preset
                    // dobla su cadencia (1080i59.94 → 1080p59.94), un cuadro por campo.
                    bool perField = Math.Abs(expectedFps - 2 * sourceFps) < 0.01 * expectedFps;
                    string parity = rig.Source.CurrentSignal.Scan is ScanType.InterlacedBff ? "bff" : "tff";
                    Assert.Contains($"bwdif=mode={(perField ? "send_field" : "send_frame")}:parity={parity}:deint=all", LastRecordingPipeline(log, "A"));
                }
                if (nvenc && !fallback)
                {
                    // Los argumentos de NVENC que una Pascal (P400) tiene que aceptar: que el archivo sea de NVENC prueba que abrió con ellos.
                    string pipeline = LastRecordingPipeline(log, "A");
                    Assert.Contains($"-c:v {FfmpegCodecMap.VideoEncoder(profile.VideoCodec)} -preset p5 -tune hq", pipeline);
                    Assert.Contains(" -bf 0", pipeline);
                }
                // El par grabado suena… salvo que la propia señal esté en silencio (emisora fuera de emisión): lo dice la alarma
                // de silencio del motor, que mide el audio de ENTRADA. Si la entrada suena y el archivo no, es un fallo.
                double volume = await MaxVolumeAsync(files[0], 0);
                if (rig.AlarmActiveBetween(AlarmType.AudioSilence, tStarted, tStop))
                    _out.WriteLine($"La señal está en silencio (alarma AudioSilence; pico grabado {volume:0.0} dBFS): el nivel no se puede medir con esta señal.");
                else
                    Assert.True(volume > -40, $"La pista de audio grabada está en silencio ({volume:0.0} dBFS).");
                Assert.Equal(0, Interlocked.Read(ref rig.Overruns));
                long dropsWhileRecording = rig.RelayDropsBetween(tStart, tStop);
                Assert.True(dropsWhileRecording == 0,
                    $"El equipo no da abasto con este preset en tiempo real: el relé descartó {dropsWhileRecording} unidades mientras grababa (cuadros perdidos en el archivo).");
            }
        }
        finally { Dump("registro", log); Cleanup(dir); }
    }

    [SkippableFact]
    public async Task RecordStopCycles_NeverReopenTheCard_NorFreezeThePreview_AndEveryFileIsValid()
    {
        Skip.If(Device is null || TestAssets.FfmpegDir is null, "Sin DeckLink configurada (BAIOSS_DECKLINK).");
        var log = new List<string>();
        var dir = NewOutputDir("cycles");
        var profile = Preset("H.264 Broadcast · 1080i59.94 MP4").ToProfile();
        try
        {
            var rig = await Rig.OpenAsync(Device!, dir, profile, "A", l => { lock (log) log.Add(l); });
            await using (rig)
            {
                await WaitUntilAsync(() => rig.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
                await WaitUntilAsync(() => rig.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en marcha");
                double first = rig.Clock.Elapsed.TotalSeconds;
                var stops = new List<double>();
                for (int i = 0; i < 8; i++)
                {
                    await rig.Engine.StartRecordingAsync(Guid.NewGuid(), profile);
                    await Task.Delay(5_000);
                    var t = Stopwatch.StartNew();
                    await rig.Engine.StopRecordingAsync();
                    stops.Add(t.Elapsed.TotalSeconds);
                    await Task.Delay(1_500);
                }
                double last = rig.Clock.Elapsed.TotalSeconds;
                await Task.Delay(1500);
                double gap = rig.LongestPreviewGap(first, last);
                _out.WriteLine($"Detener: {string.Join(" ", stops.Select(s => s.ToString("0.0", CultureInfo.InvariantCulture)))} s · hueco máximo del preview {gap * 1000:0} ms · reaperturas {rig.Receiver.Restarts}");

                Assert.Equal(0, rig.Receiver.Restarts);
                Assert.True(gap < 0.5, $"El preview se congeló {gap * 1000:0} ms en un Grabar/Detener.");
                Assert.True(stops.Max() < 3.0, $"Un Detener tardó {stops.Max():0.0} s.");
                var files = Directory.GetFiles(dir).OrderBy(f => f).ToArray();
                Assert.Equal(8, files.Length);
                foreach (var f in files) await AssertContinuousRecordingAsync(f, 5.3, rig.Source.CurrentSignal.FrameRate!.Value.Value, 1.3, "h264");
                Assert.Empty(rig.Unverified);
                Assert.Equal(0, Interlocked.Read(ref rig.RelayDrops));
            }
        }
        finally { Dump("registro", log); Cleanup(dir); }
    }

    [SkippableFact]
    public async Task TwoInputs_RecordingAtTheSameTime_ProduceTwoValidFiles()
    {
        Skip.If(Device is null || Device2 is null || TestAssets.FfmpegDir is null, "Hacen falta dos entradas con señal (BAIOSS_DECKLINK y BAIOSS_DECKLINK_2).");
        var log = new List<string>();
        var dirA = NewOutputDir("dual-A");
        var dirB = NewOutputDir("dual-B");
        var pa = Preset("H.264 Broadcast · 1080i59.94 MP4").ToProfile();
        var pb = Preset("XDCAM HD422 · 1080i59.94 50 Mbps").ToProfile();
        try
        {
            var a = await Rig.OpenAsync(Device!, dirA, pa, "A", l => { lock (log) log.Add(l); });
            await using (a)
            {
                var b = await Rig.OpenAsync(Device2!, dirB, pb, "B", l => { lock (log) log.Add(l); });
                await using (b)
                {
                    await WaitUntilAsync(() => a.Source.CurrentSignal.State == SignalState.Locked && b.Source.CurrentSignal.State == SignalState.Locked,
                        TimeSpan.FromSeconds(20), "SEÑAL OK en las dos");
                    await WaitUntilAsync(() => a.FramesBetween(0, double.MaxValue) > 30 && b.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en las dos");
                    var t = Stopwatch.StartNew();
                    double recFromA = a.Clock.Elapsed.TotalSeconds, recFromB = b.Clock.Elapsed.TotalSeconds;
                    await Task.WhenAll(a.Engine.StartRecordingAsync(Guid.NewGuid(), pa), b.Engine.StartRecordingAsync(Guid.NewGuid(), pb));
                    double started = t.Elapsed.TotalSeconds;
                    await Task.Delay(30_000);
                    double stop = t.Elapsed.TotalSeconds;
                    double recToA = a.Clock.Elapsed.TotalSeconds, recToB = b.Clock.Elapsed.TotalSeconds;
                    await Task.WhenAll(a.Engine.StopRecordingAsync(), b.Engine.StopRecordingAsync());
                    await Task.Delay(1500);
                    _out.WriteLine($"A: desbordes {a.Overruns}, descartes {a.RelayDrops} (grabando {a.RelayDropsBetween(recFromA, recToA)}) · " +
                                   $"B: desbordes {b.Overruns}, descartes {b.RelayDrops} (grabando {b.RelayDropsBetween(recFromB, recToB)})");
                    foreach (var (rig, dir, codec, from, to) in new[] { (a, dirA, "h264", recFromA, recToA), (b, dirB, "mpeg2video", recFromB, recToB) })
                    {
                        var files = Directory.GetFiles(dir);
                        Assert.Single(files);
                        await AssertContinuousRecordingAsync(files[0], stop - started + 0.3, rig.Source.CurrentSignal.FrameRate!.Value.Value, 1.5, codec);
                        Assert.Equal(0, rig.Receiver.Restarts);
                        Assert.Equal(0, rig.RelayDropsBetween(from, to));
                        Assert.Equal(0, Interlocked.Read(ref rig.Overruns));
                        Assert.Empty(rig.Unverified);
                    }
                }
            }
        }
        finally { Dump("registro", log); Cleanup(dirA); Cleanup(dirB); }
    }

    /// <summary>
    /// Dos entradas grabando con NVENC a la vez: las dos siguen en la GPU (sin degradar) y sin descartes del relé. Informa de la
    /// carga, solo preview y grabando: CPU de los ffmpeg de la prueba, del testhost y del sistema, y la GPU según nvidia-smi.
    /// Medido en el i7-8700 (12 hilos) con una Quadro P400: grabar añade ≈1,3–1,7 núcleos lógicos por canal a 1080p29.97 y
    /// ≈2,4–3,1 a 1080p59.94 (bwdif saca un cuadro por campo). Dos canales a 1080p59.94 llevan ese equipo a su techo (11,9–12
    /// hilos ocupados contando previews y drivers): una pasada sin descartes, otra con 9. Por eso esa pareja no es una fila.
    /// </summary>
    [SkippableTheory]
    [InlineData("H.264 NVENC · FullHD 1080p29.97", "H.264 NVENC · FullHD 1080p29.97")]
    [InlineData("HEVC NVENC · FullHD 1080p29.97", "HEVC NVENC · FullHD 1080p59.94")]
    public async Task TwoInputs_RecordingWithNvencAtTheSameTime_StayOnTheGpu_WithoutDrops(string presetA, string presetB)
    {
        Skip.If(Device is null || Device2 is null || TestAssets.FfmpegDir is null, "Hacen falta dos entradas con señal (BAIOSS_DECKLINK y BAIOSS_DECKLINK_2).");
        var pa = Preset(presetA).ToProfile();
        var pb = Preset(presetB).ToProfile();
        foreach (var encoder in new[] { pa, pb }.Select(p => FfmpegCodecMap.VideoEncoder(p.VideoCodec)).Distinct())
            await SkipUnlessEncoderOpensAsync(encoder);
        var log = new List<string>();
        var dirA = NewOutputDir("nvenc2-A");
        var dirB = NewOutputDir("nvenc2-B");
        var cpu = new CpuSampler(Locator().FfmpegPath);
        try
        {
            var a = await Rig.OpenAsync(Device!, dirA, pa, "A", l => { lock (log) log.Add(l); });
            await using (a)
            {
                var b = await Rig.OpenAsync(Device2!, dirB, pb, "B", l => { lock (log) log.Add(l); });
                await using (b)
                {
                    await WaitUntilAsync(() => a.Source.CurrentSignal.State == SignalState.Locked && b.Source.CurrentSignal.State == SignalState.Locked,
                        TimeSpan.FromSeconds(20), "SEÑAL OK en las dos");
                    await WaitUntilAsync(() => a.FramesBetween(0, double.MaxValue) > 30 && b.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en las dos");
                    await Task.Delay(3000);
                    cpu.Start();
                    await Task.Delay(10_000);
                    var previewOnly = cpu.Stop();
                    string gpuPreviewOnly = await NvidiaSmiAsync();

                    var t = Stopwatch.StartNew();
                    double recFromA = a.Clock.Elapsed.TotalSeconds, recFromB = b.Clock.Elapsed.TotalSeconds;
                    await Task.WhenAll(a.Engine.StartRecordingAsync(Guid.NewGuid(), pa), b.Engine.StartRecordingAsync(Guid.NewGuid(), pb));
                    double started = t.Elapsed.TotalSeconds;
                    await Task.Delay(6_000); // que se retiren los procesos de solo preview y se asiente la carga
                    cpu.Start();
                    var gpu = new List<string>();
                    for (int i = 0; i < 4; i++) { await Task.Delay(5_000); gpu.Add(await NvidiaSmiAsync()); }
                    var recording = cpu.Stop();
                    await Task.Delay(4_000);
                    double stop = t.Elapsed.TotalSeconds;
                    double recToA = a.Clock.Elapsed.TotalSeconds, recToB = b.Clock.Elapsed.TotalSeconds;
                    await Task.WhenAll(a.Engine.StopRecordingAsync(), b.Engine.StopRecordingAsync());
                    await Task.Delay(1500);

                    _out.WriteLine($"A «{presetA}» + B «{presetB}» ({Environment.ProcessorCount} núcleos lógicos)");
                    _out.WriteLine($"  Solo preview: ffmpeg {previewOnly.Ffmpeg:0.00} núcleos ({previewOnly.Processes} procesos), testhost {previewOnly.Testhost:0.00}, sistema {previewOnly.System:0.00}");
                    _out.WriteLine($"  Grabando las dos: ffmpeg {recording.Ffmpeg:0.00} núcleos ({recording.Processes} procesos), testhost {recording.Testhost:0.00}, sistema {recording.System:0.00} · " +
                                   $"lo que añade grabar ≈ {(recording.Ffmpeg - previewOnly.Ffmpeg) / 2:0.00} núcleos por canal");
                    _out.WriteLine($"  GPU [uso %, codificador %, sesiones, fps, MiB] solo preview: {gpuPreviewOnly} · grabando: {string.Join(" | ", gpu)}");
                    foreach (var (rig, key) in new[] { (a, "A"), (b, "B") })
                        lock (rig.Alarms) foreach (var al in rig.Alarms) _out.WriteLine($"  [{key}] alarma @{al.At:0.0} s {al.Type} {(al.Active ? "ON" : "off")}");

                    foreach (var (rig, dir, profile, from, to, key) in new[] { (a, dirA, pa, recFromA, recToA, "A"), (b, dirB, pb, recFromB, recToB, "B") })
                    {
                        long drops = rig.RelayDropsBetween(from, to);
                        _out.WriteLine($"  [{key}] descartes del relé grabando: {drops} · desbordes {rig.Overruns} · reaperturas de la tarjeta {rig.Receiver.Restarts}");
                        var files = Directory.GetFiles(dir);
                        Assert.Single(files);
                        Assert.False(rig.AlarmRaised(AlarmType.EncoderFallback), $"[{key}] degradó de NVENC.");
                        double fps = profile.OutputFrameRate?.Value ?? rig.Source.CurrentSignal.FrameRate!.Value.Value;
                        await AssertContinuousRecordingAsync(files[0], stop - started + 0.3, fps, 1.5, null, FfmpegCodecMap.VideoEncoder(profile.VideoCodec));
                        Assert.Equal(0, drops);
                        Assert.Equal(0, Interlocked.Read(ref rig.Overruns));
                        Assert.Equal(0, rig.Receiver.Restarts);
                        Assert.Empty(rig.Unverified);
                    }
                }
            }
        }
        finally
        {
            Dump("registro (pipelines y avisos)", log.Where(l => l.Contains("Pipeline", StringComparison.Ordinal) ||
                l.Contains("[Warning]", StringComparison.Ordinal) || l.Contains("[Error]", StringComparison.Ordinal)).TakeLast(40));
            Cleanup(dirA); Cleanup(dirB);
        }
    }

    /// <summary>
    /// Sesiones NVENC agotadas (la P400, como las GeForce, admite 8 en todo el equipo, y RustDesk ya usa una): el canal que
    /// encuentra la GPU llena degrada solo a QuickSync, con la alarma, y su archivo es válido; el que tenía sesión sigue en
    /// NVENC. Las sesiones se ocupan con procesos ficticios hasta que la GPU rechaza uno («incompatible client key»); luego se
    /// suelta uno, así que queda UNA libre para A.
    /// </summary>
    [SkippableFact]
    public async Task NvencSessionsExhausted_TheChannelThatFindsNoneFallsBackToQuickSync_AndBothFilesAreValid()
    {
        Skip.If(Device is null || Device2 is null || TestAssets.FfmpegDir is null, "Hacen falta dos entradas con señal (BAIOSS_DECKLINK y BAIOSS_DECKLINK_2).");
        await SkipUnlessEncoderOpensAsync("h264_nvenc");
        // B baja al siguiente de la cadena que abra: QuickSync si el equipo lo tiene (el i7-8700 de pruebas, sí); si no, AMF o CPU.
        string? fallbackEncoder = (await Locator().ProbeVideoEncoderAsync("h264_qsv")).Ok ? "h264_qsv" : null;
        var log = new List<string>();
        var dummies = new List<Process>();
        var dirA = NewOutputDir("nvenc-full-A");
        var dirB = NewOutputDir("nvenc-full-B");
        var profile = Preset("H.264 NVENC · FullHD 1080p29.97").ToProfile();
        try
        {
            _out.WriteLine($"Antes: [uso %, codificador %, sesiones, fps, MiB] {await NvidiaSmiAsync()}");
            string? refusal = null;
            for (int i = 0; i < 16 && refusal is null; i++)
            {
                var p = StartDummyNvencSession();
                await Task.Delay(2500);
                if (p.HasExited)
                {
                    refusal = (await p.StandardError.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault() ?? $"exit {p.ExitCode}";
                    p.Dispose();
                }
                else dummies.Add(p);
            }
            _out.WriteLine($"La GPU admitió {dummies.Count} sesiones ficticias además de las ajenas; la siguiente: «{refusal}» · {await NvidiaSmiAsync()}");
            Assert.True(dummies.Count > 0, $"El primer proceso NVENC ficticio no arrancó: «{refusal}».");
            Skip.If(refusal is null, $"La GPU admitió {dummies.Count} sesiones NVENC sin agotarse (sin límite de sesiones): no hay nada que agotar.");
            KillQuietly(dummies[^1]);
            dummies.RemoveAt(dummies.Count - 1);
            await Task.Delay(2000);
            _out.WriteLine($"Con una libre: {await NvidiaSmiAsync()}");

            var a = await Rig.OpenAsync(Device!, dirA, profile, "A", l => { lock (log) log.Add(l); });
            await using (a)
            {
                var b = await Rig.OpenAsync(Device2!, dirB, profile, "B", l => { lock (log) log.Add(l); });
                await using (b)
                {
                    await WaitUntilAsync(() => a.Source.CurrentSignal.State == SignalState.Locked && b.Source.CurrentSignal.State == SignalState.Locked,
                        TimeSpan.FromSeconds(20), "SEÑAL OK en las dos");
                    await WaitUntilAsync(() => a.FramesBetween(0, double.MaxValue) > 30 && b.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en las dos");
                    // A se lleva la única sesión libre; B, tres segundos después, ya no encuentra ninguna.
                    var t = Stopwatch.StartNew();
                    await a.Engine.StartRecordingAsync(Guid.NewGuid(), profile);
                    double startedA = t.Elapsed.TotalSeconds;
                    await Task.Delay(3000);
                    double startB = t.Elapsed.TotalSeconds;
                    await b.Engine.StartRecordingAsync(Guid.NewGuid(), profile);
                    double startedB = t.Elapsed.TotalSeconds;
                    await Task.Delay(15_000);
                    _out.WriteLine($"Grabando las dos: {await NvidiaSmiAsync()}");
                    double stop = t.Elapsed.TotalSeconds;
                    await Task.WhenAll(a.Engine.StopRecordingAsync(), b.Engine.StopRecordingAsync());
                    await Task.Delay(1500);
                    _out.WriteLine($"Grabar B tardó {startedB - startB:0.00} s");
                    foreach (var (rig, key) in new[] { (a, "A"), (b, "B") })
                        lock (rig.Alarms) foreach (var al in rig.Alarms) _out.WriteLine($"  [{key}] alarma @{al.At:0.0} s {al.Type} {(al.Active ? "ON" : "off")}");

                    Assert.False(a.AlarmRaised(AlarmType.EncoderFallback), "A tenía sesión y aun así degradó.");
                    Assert.True(b.AlarmRaised(AlarmType.EncoderFallback), "B no encontró sesión NVENC y no avisó de la degradación.");
                    var fa = Directory.GetFiles(dirA);
                    var fb = Directory.GetFiles(dirB);
                    _out.WriteLine("Archivos A: " + string.Join(", ", fa.Select(Path.GetFileName)) + " · B: " + string.Join(", ", fb.Select(Path.GetFileName)));
                    // Una pieza por canal: el intento de B que no abrió no deja un archivo vacío.
                    Assert.Single(fa);
                    Assert.Single(fb);
                    double fps = profile.OutputFrameRate!.Value.Value;
                    await AssertContinuousRecordingAsync(fa[0], stop - startedA + 0.3, fps, 1.5, "h264", "h264_nvenc");
                    // B pierde el arranque del intento que no abrió: algo más de margen.
                    await AssertContinuousRecordingAsync(fb[0], stop - startedB + 0.3, fps, 2.5, "h264", fallbackEncoder);
                    Assert.Empty(a.Unverified);
                    Assert.Empty(b.Unverified);
                }
            }
        }
        finally
        {
            foreach (var d in dummies) KillQuietly(d);
            Dump("registro (pipelines y avisos)", log.Where(l => l.Contains("Pipeline", StringComparison.Ordinal) ||
                l.Contains("[Warning]", StringComparison.Ordinal) || l.Contains("[Error]", StringComparison.Ordinal)).TakeLast(40));
            Cleanup(dirA); Cleanup(dirB);
        }
    }

    [SkippableFact]
    public async Task InputWithoutSignal_WaitsWithoutSpinning_AndRefusesToRecordCleanly()
    {
        Skip.If(NoSignalDevice is null || TestAssets.FfmpegDir is null, "Sin entrada sin señal configurada (BAIOSS_DECKLINK_NOSIGNAL).");
        var log = new List<string>();
        var dir = NewOutputDir("nosignal");
        int before = FfmpegProcessCount();
        var profile = Preset("H.264 Broadcast · 1080i59.94 MP4").ToProfile();
        try
        {
            var rig = await Rig.OpenAsync(NoSignalDevice!, dir, profile, "C", l => { lock (log) log.Add(l); });
            await using (rig)
            {
                await Task.Delay(15_000);
                lock (rig.Signals) foreach (var s in rig.Signals) _out.WriteLine($"  señal @{s.At:0.0} s {s.State} «{s.Label}»");
                _out.WriteLine($"Reaperturas del receptor en 15 s: {rig.Receiver.Restarts}; frames de preview: {rig.FramesBetween(0, double.MaxValue)}");
                Assert.Equal(SignalState.NoSignal, rig.Source.CurrentSignal.State);
                Assert.False(string.IsNullOrWhiteSpace(rig.Source.CurrentSignal.FormatLabel), "Sin señal, pero sin decir por qué.");
                Assert.Equal(0, rig.FramesBetween(0, double.MaxValue));
                // Reintenta abrir la tarjeta con calma (no en bucle sin pausa).
                Assert.InRange(rig.Receiver.Restarts, 0, 8);

                // Grabar sin señal se rechaza y el canal vuelve a reposo (no queda «Iniciando» para siempre).
                await Assert.ThrowsAnyAsync<Exception>(() => rig.Engine.StartRecordingAsync(Guid.NewGuid(), profile));
                Assert.Equal(RecordingState.Idle, rig.Engine.State);
                Assert.Empty(Directory.GetFiles(dir));
            }
            await Task.Delay(1500);
            Assert.True(FfmpegProcessCount() <= before, "Quedaron procesos FFmpeg vivos tras cerrar el canal sin señal.");
        }
        finally { Dump("registro", log); Cleanup(dir); }
    }

    [SkippableFact]
    public async Task SameInputAssignedTwice_TheSecondReportsBusy_AndTakesTheCardWhenFreed()
    {
        Skip.If(Device is null || TestAssets.FfmpegDir is null, "Sin DeckLink configurada (BAIOSS_DECKLINK).");
        var log = new List<string>();
        var dir = NewOutputDir("busy");
        var profile = Preset("H.264 Broadcast · 1080i59.94 MP4").ToProfile();
        try
        {
            var first = await Rig.OpenAsync(Device!, dir, profile, "A", l => { lock (log) log.Add(l); });
            Rig? second = null;
            try
            {
                await WaitUntilAsync(() => first.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK en el primero");
                second = await Rig.OpenAsync(Device!, dir, profile, "B", l => { lock (log) log.Add(l); });
                await Task.Delay(8_000);
                _out.WriteLine($"Segundo con la tarjeta ocupada: {second.Source.CurrentSignal.State} «{second.Source.CurrentSignal.FormatLabel}»");
                Assert.Equal(SignalState.NoSignal, second.Source.CurrentSignal.State);
                // En autodetección FFmpeg no dice «en uso», solo el fallo genérico, pero al instante: el panel lo nombra así y no
                // como «La tarjeta no detecta señal».
                Assert.Equal(Baioss.Record.Application.Localization.Localizer.T("Dl_State_Refused"), second.Source.CurrentSignal.FormatLabel);
                Assert.Equal(SignalState.Locked, first.Source.CurrentSignal.State);
                Assert.True(first.LongestPreviewGap(first.Clock.Elapsed.TotalSeconds - 7, first.Clock.Elapsed.TotalSeconds) < 0.25, "El intento del segundo interrumpió al primero.");
            }
            finally { await first.DisposeAsync(); }

            // Liberada la tarjeta, el segundo la toma solo (sus reintentos) y pinta.
            var sw = Stopwatch.StartNew();
            await WaitUntilAsync(() => second!.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(30), "el segundo toma la tarjeta");
            double t = second!.Clock.Elapsed.TotalSeconds;
            await WaitUntilAsync(() => second.FramesBetween(t, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview del segundo");
            _out.WriteLine($"El segundo tomó la tarjeta {sw.Elapsed.TotalSeconds:0.0} s después de liberarla.");
            await second.DisposeAsync();
        }
        finally { Dump("registro", log); Cleanup(dir); }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureProcessDeath_WhileRecording_ContinuesInANewPiece(bool slateOnSignalLoss)
    {
        Skip.If(Device is null || TestAssets.FfmpegDir is null, "Sin DeckLink configurada (BAIOSS_DECKLINK).");
        var log = new List<string>();
        var dir = NewOutputDir(slateOnSignalLoss ? "death-slate" : "death");
        var profile = Preset("H.264 Broadcast · 1080i59.94 MP4").ToProfile();
        profile.SlateOnSignalLoss = slateOnSignalLoss;
        try
        {
            var rig = await Rig.OpenAsync(Device!, dir, profile, "A", l => { lock (log) log.Add(l); });
            await using (rig)
            {
                await WaitUntilAsync(() => rig.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
                await WaitUntilAsync(() => rig.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en marcha");
                await rig.Engine.StartRecordingAsync(Guid.NewGuid(), profile);
                await Task.Delay(8_000);
                double tKill = rig.Clock.Elapsed.TotalSeconds;
                Assert.True(rig.Receiver.KillProcessForTest(), "No había proceso de captura que matar.");
                // La tarjeta se reabre sola y la grabación sigue (pieza nueva).
                await WaitUntilAsync(() => rig.Receiver.Restarts >= 1 && rig.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(30), "la tarjeta reabre");
                await WaitUntilAsync(() => rig.FramesBetween(tKill + 0.5, double.MaxValue) > 30, TimeSpan.FromSeconds(30), "preview de vuelta");
                await Task.Delay(10_000);
                await rig.Engine.StopRecordingAsync();
                await Task.Delay(2000);

                double backAt; lock (rig.Frames) backAt = rig.Frames.First(f => f > tKill + 0.3);
                _out.WriteLine($"Interrupciones: {string.Join("; ", rig.Interruptions.Select(i => $"{i.Code} {i.Reason}"))} · preview de vuelta {backAt - tKill:0.0} s tras la caída");
                lock (rig.Alarms) foreach (var a in rig.Alarms) _out.WriteLine($"  alarma @{a.At:0.0} s {a.Type} {(a.Active ? "ON" : "off")}");
                var files = Directory.GetFiles(dir).OrderBy(f => File.GetCreationTimeUtc(f)).ToArray();
                _out.WriteLine("Piezas: " + string.Join(", ", files.Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1_000_000.0:0.0} MB)")));
                Assert.True(files.Length >= 2, "La grabación no continuó en una pieza nueva.");
                if (slateOnSignalLoss)
                {
                    // La fuente avisa de la pérdida al instante: carta de ajuste (su propia pieza) y vuelta a la señal.
                    lock (rig.Alarms)
                    {
                        Assert.Contains(rig.Alarms, a => a.Type == AlarmType.Slate && a.Active);
                        Assert.Contains(rig.Alarms, a => a.Type == AlarmType.Slate && !a.Active);
                    }
                }
                else
                {
                    Assert.Single(rig.Interruptions); // sin carta: el proceso acaba por EOF y se recupera en pieza nueva
                    // …en cuanto la tarjeta reabre (~1,3 s), no al siguiente backoff: antes, el reintento que salía justo antes de que
                    // volviera la señal fallaba y el siguiente llegaba 2 s después (3,3 s de hueco medidos).
                    Assert.True(backAt - tKill < 2.5, $"La grabación volvió {backAt - tKill:0.0} s después de la caída: esperó al backoff.");
                }
                Assert.Equal(RecordingState.Idle, rig.Engine.State);
                Assert.Empty(rig.Unverified);
                foreach (var f in files)
                {
                    var m = await ProbeAsync(f);
                    _out.WriteLine($"  {Path.GetFileName(f)}: {Describe(m)}");
                    Assert.True(m.Video is not null && m.Video.Packets > 0, $"Pieza ilegible: {f}");
                }
                // La primera pieza conserva lo grabado antes de la caída (no se truncó al relanzar).
                var firstPiece = await ProbeAsync(files[0]);
                Assert.True(firstPiece.Video!.Duration >= 6.5, $"La primera pieza quedó en {firstPiece.Video.Duration:0.0} s.");
                Assert.Equal(1, rig.Receiver.Restarts);
            }
        }
        finally { Dump("registro", log); Cleanup(dir); }
    }

    /// <summary>
    /// Muere el proceso de GRABACIÓN (no la captura): la grabación sigue en una pieza nueva y la tarjeta no se reabre. Con
    /// fMP4 la pieza cortada conserva lo grabado; con MP4 estándar (la build publicada) queda sin índice: lo mínimo es que
    /// el operador se entere (alarma «grabación sin verificar» + aviso a la auditoría).
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordingProcessKilled_ContinuesInANewPiece_AndTheCutPieceIsReadableOrFlagged(bool fragmentedMp4)
    {
        Skip.If(Device is null || TestAssets.FfmpegDir is null, "Sin DeckLink configurada (BAIOSS_DECKLINK).");
        var log = new List<string>();
        var dir = NewOutputDir(fragmentedMp4 ? "kill-fmp4" : "kill-mp4");
        var profile = Preset("H.264 Broadcast · 1080i59.94 MP4").ToProfile();
        try
        {
            var rig = await Rig.OpenAsync(Device!, dir, profile, "A", l => { lock (log) log.Add(l); }, fragmentedMp4);
            await using (rig)
            {
                await WaitUntilAsync(() => rig.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
                await WaitUntilAsync(() => rig.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en marcha");
                await rig.Engine.StartRecordingAsync(Guid.NewGuid(), profile);
                await Task.Delay(10_000);
                double tKill = rig.Clock.Elapsed.TotalSeconds;
                Assert.True(rig.Engine.KillRecorderProcessForTest(), "No había proceso de grabación que matar.");
                await WaitUntilAsync(() => Directory.GetFiles(dir).Length >= 2, TimeSpan.FromSeconds(20), "pieza nueva");
                await Task.Delay(8_000);
                await rig.Engine.StopRecordingAsync();
                await Task.Delay(2000);

                var files = Directory.GetFiles(dir).OrderBy(f => File.GetCreationTimeUtc(f)).ToArray();
                _out.WriteLine("Piezas: " + string.Join(", ", files.Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1_000_000.0:0.0} MB)")));
                lock (rig.Alarms) foreach (var a in rig.Alarms) _out.WriteLine($"  alarma @{a.At:0.0} s {a.Type} {(a.Active ? "ON" : "off")}");
                Assert.Equal(2, files.Length);
                Assert.Single(rig.Interruptions);
                Assert.Equal(0, rig.Receiver.Restarts); // la captura no se tocó: solo murió el proceso del canal
                await AssertContinuousRecordingAsync(files[1], 8.0, rig.Source.CurrentSignal.FrameRate!.Value.Value, 2.0, "h264");
                var (_, err, exit) = await RunAsync(Locator().FfprobePath, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", files[0]);
                _out.WriteLine($"Pieza cortada: ffprobe {exit} «{err.Trim()}»");
                if (fragmentedMp4)
                {
                    var cut = await ProbeAsync(files[0]);
                    Assert.True(cut.Video!.Duration >= 8.0, $"La pieza cortada solo conserva {cut.Video.Duration:0.0} s de ~10.");
                    Assert.Empty(rig.Unverified);
                }
                else
                {
                    // Riesgo conocido del MP4 estándar (docs/INCIDENTE-2026-09-06.md): sin moov. Que al menos se avise.
                    Assert.NotEqual(0, exit);
                    Assert.True(rig.AlarmRaised(AlarmType.RecordingUnverified), "La pieza ilegible no levantó la alarma.");
                    Assert.Single(rig.Unverified);
                }
            }
        }
        finally { Dump("registro", log); Cleanup(dir); }
    }

    [SkippableTheory]
    [InlineData(AudioTrackMode.PairsAsTracks, ContainerFormat.Mp4, AudioCodec.Aac, 8, 2)]
    [InlineData(AudioTrackMode.Multichannel, ContainerFormat.Mkv, AudioCodec.Pcm, 1, 16)]
    [InlineData(AudioTrackMode.Multichannel, ContainerFormat.Mxf, AudioCodec.Pcm, 1, 16)]
    public async Task SixteenEmbeddedChannels_AreRecordedAsTheTrackModeSays(AudioTrackMode mode, ContainerFormat container, AudioCodec audio, int tracks, int channelsPerTrack)
    {
        Skip.If(Device is null || TestAssets.FfmpegDir is null, "Sin DeckLink configurada (BAIOSS_DECKLINK).");
        var log = new List<string>();
        var dir = NewOutputDir($"audio16-{mode}-{container}");
        var profile = Preset("H.264 Broadcast · 1080i59.94 MP4").ToProfile();
        profile.Container = container;
        profile.AudioCodec = audio;
        profile.AudioTracks = mode;
        if (container is ContainerFormat.Mxf)
        {
            // MXF no lleva H.264 de x264 como en MP4: el formato de entrega es XDCAM (MPEG-2 4:2:2).
            profile = Preset("XDCAM HD422 · 1080i59.94 50 Mbps").ToProfile();
            profile.AudioTracks = mode;
        }
        try
        {
            var rig = await Rig.OpenAsync(Device!, dir, profile, "A", l => { lock (log) log.Add(l); },
                (AudioSelection.ChannelsKey, "16"), (AudioSelection.PairsKey, AudioSelection.AllValue));
            await using (rig)
            {
                await WaitUntilAsync(() => rig.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
                await WaitUntilAsync(() => rig.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en marcha");
                Assert.Equal(16, rig.Source.CurrentSignal.AudioChannels);
                var t = Stopwatch.StartNew();
                await rig.Engine.StartRecordingAsync(Guid.NewGuid(), profile);
                double started = t.Elapsed.TotalSeconds;
                await Task.Delay(10_000);
                double stop = t.Elapsed.TotalSeconds;
                await rig.Engine.StopRecordingAsync();
                await Task.Delay(1500);
                var files = Directory.GetFiles(dir);
                Assert.Single(files);
                var m = await AssertContinuousRecordingAsync(files[0], stop - started + 0.3, rig.Source.CurrentSignal.FrameRate!.Value.Value, 1.5, null);
                var audioStreams = m.Audio.ToArray();
                Assert.Equal(tracks, audioStreams.Length);
                Assert.All(audioStreams, s => Assert.Equal(channelsPerTrack, s.Channels));
                // El par 1 de la señal tiene sonido; el resto del SDI está en silencio: la primera pista suena, y con una
                // pista por par, las demás no (los canales no se mezclaron entre pares).
                double first = await MaxVolumeAsync(files[0], 0);
                _out.WriteLine($"Pista 1: {first:0.0} dBFS");
                Assert.True(first > -40, "La primera pista está en silencio.");
                if (tracks > 1)
                {
                    double second = await MaxVolumeAsync(files[0], 1);
                    _out.WriteLine($"Pista 2: {second:0.0} dBFS");
                    Assert.True(second < -60, $"La pista del par 2 suena ({second:0.0} dBFS): se mezclaron canales.");
                }
            }
        }
        finally { Dump("registro", log); Cleanup(dir); }
    }

    /// <summary>Grabación larga (BAIOSS_HW_LONG_MINUTES): sin deriva entre audio y vídeo, sin huecos y sin frames perdidos.</summary>
    [SkippableFact]
    public async Task LongRecording_HasNoAudioVideoDrift_NorGaps()
    {
        Skip.If(Device is null || TestAssets.FfmpegDir is null, "Sin DeckLink configurada (BAIOSS_DECKLINK).");
        Skip.IfNot(int.TryParse(Env("BAIOSS_HW_LONG_MINUTES"), out int minutes) && minutes > 0, "Grabación larga: BAIOSS_HW_LONG_MINUTES.");
        var log = new List<string>();
        var dirs = new List<string>();
        var rigs = new List<(Rig Rig, string Dir, RecordingProfile Profile)>();
        try
        {
            foreach (var (device, key, preset) in new[] { (Device!, "A", "H.264 Broadcast · 1080i59.94 MP4"), (Device2, "B", "H.264 NVENC · FullHD 1080p29.97") })
            {
                if (device is null) continue;
                var dir = NewOutputDir("long-" + key);
                dirs.Add(dir);
                var profile = Preset(preset).ToProfile();
                rigs.Add((await Rig.OpenAsync(device, dir, profile, key, l => { lock (log) log.Add(l); }), dir, profile));
            }
            foreach (var (rig, _, _) in rigs)
            {
                await WaitUntilAsync(() => rig.Source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
                await WaitUntilAsync(() => rig.FramesBetween(0, double.MaxValue) > 30, TimeSpan.FromSeconds(20), "preview en marcha");
            }
            var me = Process.GetCurrentProcess();
            long memBefore = me.PrivateMemorySize64; int handlesBefore = me.HandleCount;
            var t = Stopwatch.StartNew();
            await Task.WhenAll(rigs.Select(r => r.Rig.Engine.StartRecordingAsync(Guid.NewGuid(), r.Profile)));
            double started = t.Elapsed.TotalSeconds;
            for (int m = 0; m < minutes; m++)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                me.Refresh();
                _out.WriteLine($"{m + 1} min: memoria privada {me.PrivateMemorySize64 / 1_000_000} MB, handles {me.HandleCount}, " +
                               string.Join(" · ", rigs.Select(r => $"{r.Rig.Engine.Stats.OutputFps:0.0} fps drop {r.Rig.Engine.Stats.DroppedFrames} dup {r.Rig.Engine.Stats.DuplicatedFrames} relé {r.Rig.RelayDrops}")));
            }
            double stop = t.Elapsed.TotalSeconds;
            await Task.WhenAll(rigs.Select(r => r.Rig.Engine.StopRecordingAsync()));
            await Task.Delay(3000);
            me.Refresh();
            _out.WriteLine($"Memoria privada {memBefore / 1_000_000} → {me.PrivateMemorySize64 / 1_000_000} MB; handles {handlesBefore} → {me.HandleCount}");
            foreach (var (rig, dir, profile) in rigs)
            {
                var files = Directory.GetFiles(dir);
                Assert.Single(files);
                double fps = profile.OutputFrameRate?.Value ?? rig.Source.CurrentSignal.FrameRate!.Value.Value;
                var media = await AssertContinuousRecordingAsync(files[0], stop - started + 0.3, fps, 2.0, null);
                // Deriva: el audio debe durar lo mismo que el vídeo al final de la grabación (a 25 ms por hora como mucho).
                var v = media.Video!; var a = media.Audio.First();
                double drift = a.Duration - v.Duration;
                _out.WriteLine($"{Path.GetFileName(files[0])}: deriva A−V al final {drift * 1000:0} ms en {v.Duration / 60:0.0} min");
                Assert.True(Math.Abs(drift) < 0.1, $"Deriva audio/vídeo de {drift * 1000:0} ms.");
                Assert.Equal(0, Interlocked.Read(ref rig.RelayDrops));
                Assert.Equal(0, Interlocked.Read(ref rig.Overruns));
                Assert.Empty(rig.Interruptions);
            }
        }
        finally
        {
            foreach (var (rig, _, _) in rigs) await rig.DisposeAsync();
            Dump("registro", log.TakeLast(400));
            foreach (var d in dirs) Cleanup(d);
        }
    }
}
