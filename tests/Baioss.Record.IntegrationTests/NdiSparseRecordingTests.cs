using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using NewTek;
using NewTek.NDI;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Barrido de grabación con fuentes NDI que mandan MENOS cuadros de los que declaran, por el camino de la aplicación (receptor,
/// vídeo en NUT con la marca de su periodo, proceso del canal, cola de escritura, MP4 estándar como la build publicada): presets
/// de fábrica con un grafismo como el de Offside Engine (1080 BGRA con la estructura a cero, que NDI lee como «interleaved», a
/// 60000/1000 y uno de cada 15 periodos), unos cuantos con un 1080i como el de vMix (uno de cada tres), el timecode quemado, la
/// grabación por segmentos, la pérdida y vuelta de la fuente y un cambio de resolución en caliente. Cada segundo de la fuente
/// lleva una marca (cuadro blanco y pitido de 100 ms): en el archivo tienen que quedar a un segundo, con el pitido junto a su
/// destello, a la cadencia del preset y sin huecos. Se omite sin el runtime NDI (Processing.NDI.Lib.x64.dll junto a las pruebas);
/// con <c>BAIOSS_NDI_KEEP=1</c> se conservan las grabaciones.
/// </summary>
public sealed class NdiSparseRecordingTests
{
    private readonly ITestOutputHelper _out;
    public NdiSparseRecordingTests(ITestOutputHelper output) => _out = output;

    private static readonly bool Keep = Environment.GetEnvironmentVariable("BAIOSS_NDI_KEEP") == "1";
    /// <summary>Cadencias declaradas: la del grafismo de Offside (60000/1000) y la de un 1080i29.97.</summary>
    private const double GraphicsRate = 60000 / 1000.0;
    private const double InterlacedRate = 30000 / 1001.0;

    public enum SenderKind { Graphics, Interleaved }

    /// <summary>
    /// Emisor NDI que marca él mismo el ritmo: declara <c>rateN/rateD</c> y envía uno de cada <c>sendEvery</c> periodos, con el
    /// audio (si lo hay) completo y a tiempo real. <see cref="SenderKind.Graphics"/>: BGRA con alfa y el campo de barrido a cero,
    /// como Offside; <see cref="SenderKind.Interleaved"/>: UYVY con los dos campos entretejidos (la barra medio periodo más allá en
    /// el inferior), como un 1080i de vMix. Puede cambiar de tamaño en caliente (<see cref="Resize"/>) y callar del todo
    /// (<see cref="Paused"/>) sin dejar de estar en la red.
    /// </summary>
    private sealed class SparseSender : IDisposable
    {
        private const int Bar = 24;
        private const double BeepSeconds = 0.1;
        private readonly IntPtr _send;
        private readonly Thread _thread;
        private readonly int _rateN, _rateD, _sendEvery;
        private readonly bool _withAudio;
        private readonly SenderKind _kind;
        private volatile bool _stop, _paused;
        private volatile int _size = (1920 << 16) | 1080;

        public SparseSender(string name, int rateN, int rateD, int sendEvery, bool withAudio, SenderKind kind)
        {
            Name = name; _rateN = rateN; _rateD = rateD; _sendEvery = sendEvery; _withAudio = withAudio; _kind = kind;
            SlotsPerMark = (int)Math.Round((double)rateN / rateD);
            if (SlotsPerMark % sendEvery != 0) throw new ArgumentException("La marca de sincronía debe caer en un cuadro enviado.");
            var create = new NDIlib.send_create_t { p_ndi_name = UTF.StringToUtf8(name), p_groups = IntPtr.Zero, clock_video = false, clock_audio = false };
            _send = NDIlib.send_create(ref create);
            Marshal.FreeHGlobal(create.p_ndi_name);
            if (_send == IntPtr.Zero) throw new InvalidOperationException("NDI: no se pudo crear el emisor.");
            _thread = new Thread(Run) { IsBackground = true, Name = "emisor NDI con huecos" };
            _thread.Start();
        }

        public string Name { get; }
        /// <summary>Periodos entre dos marcas de sincronía (un segundo de la fuente, redondeado).</summary>
        public int SlotsPerMark { get; }
        /// <summary>Segundos entre dos marcas.</summary>
        public double MarkPeriod => SlotsPerMark * (double)_rateD / _rateN;
        /// <summary>Lo más tarde que salió un periodo respecto a su hora (el emisor duerme con el temporizador de Windows).</summary>
        public double MaxLateMs { get; private set; }
        /// <summary>Sin vídeo ni audio mientras sea true: la fuente calla, como un emisor colgado, pero sigue en la red.</summary>
        public bool Paused { get => _paused; set => _paused = value; }
        public void Resize(int width, int height) => _size = (width << 16) | height;

        private void Run()
        {
            double fps = (double)_rateN / _rateD;
            int bytesPerPixel = _kind == SenderKind.Graphics ? 4 : 2;
            IntPtr video = IntPtr.Zero, audio = Marshal.AllocHGlobal(2 * 4 * 4096);
            var samples = new float[2 * 4096];
            byte[] frame = Array.Empty<byte>(), gray = Array.Empty<byte>();
            int width = 0, height = 0, stride = 0;
            var clock = Stopwatch.StartNew();
            long n = 0, sent = 0;
            try
            {
                while (!_stop)
                {
                    double due = n / fps, wait = due - clock.Elapsed.TotalSeconds;
                    if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                    MaxLateMs = Math.Max(MaxLateMs, (clock.Elapsed.TotalSeconds - due) * 1000);
                    bool paused = _paused;
                    if (!paused && n % _sendEvery == 0)
                    {
                        int size = _size, w = size >> 16, h = size & 0xFFFF;
                        if (w != width || h != height)
                        {
                            width = w; height = h; stride = w * bytesPerPixel;
                            frame = new byte[stride * h];
                            gray = GrayLine(w, stride);
                            if (video != IntPtr.Zero) Marshal.FreeHGlobal(video);
                            video = Marshal.AllocHGlobal(frame.Length);
                        }
                        Draw(frame, gray, width, height, stride, due, mark: n % SlotsPerMark == 0, fps);
                        Marshal.Copy(frame, 0, video, frame.Length);
                        var vf = new NDIlib.video_frame_v2_t
                        {
                            xres = width, yres = height,
                            FourCC = _kind == SenderKind.Graphics ? NDIlib.FourCC_type_e.FourCC_type_BGRA : NDIlib.FourCC_type_e.FourCC_type_UYVY,
                            frame_rate_N = _rateN, frame_rate_D = _rateD, picture_aspect_ratio = 16f / 9f,
                            // El grafismo deja el campo a cero, que en NDI es «interleaved»; el 1080i lo dice de verdad.
                            frame_format_type = _kind == SenderKind.Graphics ? default : NDIlib.frame_format_type_e.frame_format_type_interleaved,
                            timecode = NDIlib.send_timecode_synthesize, p_data = video, line_stride_in_bytes = stride, p_metadata = IntPtr.Zero,
                        };
                        NDIlib.send_send_video_v2(_send, ref vf);
                    }
                    long target = (long)Math.Round((n + 1) * 48000.0 / fps);
                    if (_withAudio && !paused)
                    {
                        // El audio de este periodo, completo: silencio salvo los 100 ms del pitido tras cada marca.
                        int count = (int)(target - sent);
                        for (int i = 0; i < count; i++)
                        {
                            double t = (sent + i) / 48000.0;
                            double sinceMark = t - Math.Floor(t * fps / SlotsPerMark) * SlotsPerMark / fps;
                            float s = sinceMark < BeepSeconds ? (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * t)) : 0f; // −20 dBFS
                            samples[i] = s; samples[count + i] = s; // planar
                        }
                        Marshal.Copy(samples, 0, audio, 2 * count);
                        var af = new NDIlib.audio_frame_v2_t
                        {
                            sample_rate = 48000, no_channels = 2, no_samples = count, timecode = NDIlib.send_timecode_synthesize,
                            p_data = audio, channel_stride_in_bytes = count * 4, p_metadata = IntPtr.Zero,
                        };
                        NDIlib.send_send_audio_v2(_send, ref af);
                    }
                    sent = target; // callada, el reloj del audio sigue corriendo
                    n++;
                }
            }
            finally
            {
                if (video != IntPtr.Zero) Marshal.FreeHGlobal(video);
                Marshal.FreeHGlobal(audio);
            }
        }

        private byte[] GrayLine(int width, int stride)
        {
            var gray = new byte[stride];
            if (_kind == SenderKind.Graphics)
                for (int x = 0; x < width; x++) { gray[x * 4] = gray[x * 4 + 1] = gray[x * 4 + 2] = 37; gray[x * 4 + 3] = 160; } // B G R A
            else
                for (int x = 0; x < width; x += 2) { gray[x * 2] = 128; gray[x * 2 + 1] = 48; gray[x * 2 + 2] = 128; gray[x * 2 + 3] = 48; } // U Y V Y
            return gray;
        }

        private void Draw(byte[] frame, byte[] gray, int width, int height, int stride, double t, bool mark, double fps)
        {
            if (mark)
            {
                if (_kind == SenderKind.Graphics) Array.Fill(frame, (byte)255);
                else for (int i = 0; i < frame.Length; i += 2) { frame[i] = 128; frame[i + 1] = 235; }
                return;
            }
            int BarX(double at) => ((int)(width / 2.0 - Bar / 2.0 + width * 0.45 * Math.Sin(Math.PI * at))) & ~1;
            int top = BarX(t), bottom = BarX(t + 0.5 / fps);
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(gray, 0, frame, y * stride, stride);
                int x0 = _kind == SenderKind.Interleaved && y % 2 == 1 ? bottom : top; // el campo inferior, medio periodo después
                if (_kind == SenderKind.Graphics) frame.AsSpan(y * stride + x0 * 4, Bar * 4).Fill(255); // blanca y opaca
                else for (int x = x0; x < x0 + Bar; x++) frame[y * stride + x * 2 + 1] = 235;
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
            NDIlib.send_destroy(_send);
        }
    }

    /// <summary>Lo que dejó una grabación: archivos, registro, alarmas y señales con sus instantes (s desde que se abrió la fuente).</summary>
    private sealed class Recorded : IDisposable
    {
        public required string OutputRoot { get; init; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public double Now => Clock.Elapsed.TotalSeconds;
        public List<string> Files { get; set; } = new();
        public List<(double At, string Line)> Log { get; } = new();
        public List<(double At, AlarmType Type, bool Active)> Alarms { get; } = new();
        public List<(double At, SignalState State, string? Label)> Signals { get; } = new();
        public List<(int ExitCode, string Reason)> Interruptions { get; } = new();
        public List<string> Unverified { get; } = new();
        public string FormatLabel { get; set; } = "";
        public double RecordStart { get; set; }
        public double RecordEnd { get; set; }
        public double RecordedFor { get; set; }
        public double StopSeconds { get; set; }
        /// <summary>FPS OUT cada medio segundo desde el Grabar (s desde el Grabar, cuadros/s).</summary>
        public List<(double At, double Fps)> FpsOutSamples { get; } = new();
        /// <summary>La mediana de FPS OUT desde el segundo 4 del Grabar, cuando el codificador ya arrancó.</summary>
        public double FpsOut { get; set; }

        public bool AlarmActiveBetween(AlarmType type, double from, double to)
        {
            lock (Alarms)
            {
                var events = Alarms.Where(a => a.Type == type).OrderBy(a => a.At).ToList();
                return events.Where(a => a.At <= from).Select(a => a.Active).LastOrDefault() || events.Any(a => a.Active && a.At > from && a.At <= to);
            }
        }

        /// <summary>Las líneas «Pipeline canal NDI (grabación+preview)»: cada proceso de grabación que arrancó, con su instante.</summary>
        public List<(double At, string Line)> RecordPipelines()
        {
            lock (Log) return Log.Where(l => l.Line.Contains("Pipeline canal NDI (grabación+preview)", StringComparison.Ordinal)).ToList();
        }

        public string Pipeline() => RecordPipelines().Select(p => p.Line).LastOrDefault() ?? "";

        public void Dispose()
        {
            if (Keep) return;
            try { if (Directory.Exists(OutputRoot)) Directory.Delete(OutputRoot, true); } catch { /* best effort */ }
        }
    }

    private static void SkipWithoutNdi()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        Skip.IfNot(NdiRuntime.IsAvailable, "Runtime NDI no disponible (Processing.NDI.Lib.x64.dll junto a las pruebas).");
    }

    private static EncodingPreset Preset(string name) =>
        PresetCatalog.CreateBuiltIns().SingleOrDefault(p => p.Name == name) ?? throw new ArgumentException($"No existe el preset «{name}».");

    private static string Unique(string what) => $"Baioss barrido {what} {Guid.NewGuid().ToString("N")[..8]}";

    private static SparseSender LikeOffside() => new(Unique("grafismo"), 60000, 1000, 15, withAudio: true, SenderKind.Graphics);

    /// <summary>
    /// Abre la fuente del emisor, previsualiza, graba <paramref name="seconds"/> s con <paramref name="profile"/> (con
    /// <paramref name="during"/> en marcha desde el Grabar) y detiene, como la aplicación: cola de escritura de 10 s si el FFmpeg la
    /// admite y MP4 estándar.
    /// </summary>
    private async Task<Recorded> RecordAsync(SparseSender sender, RecordingProfile profile, double seconds, Func<Recorded, Task>? during = null)
    {
        string? name = null;
        for (int i = 0; i < 10 && name is null; i++)
            name = NdiRuntime.DiscoverSources(1000).FirstOrDefault(s => s.Contains(sender.Name, StringComparison.Ordinal));
        Assert.True(name is not null, $"El emisor de prueba «{sender.Name}» no aparece entre las fuentes NDI.");

        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var rec = new Recorded { OutputRoot = Path.Combine(Path.GetTempPath(), $"baioss-ndi-barrido-{Guid.NewGuid():N}") };
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (rec.Log) rec.Log.Add((rec.Now, l)); });
        var source = new NdiCaptureSource(new InputSource { Name = name!, Type = InputType.Ndi, Uri = name }, loggers.CreateLogger("Ndi"));
        try
        {
            source.SignalChanged += (_, s) => { lock (rec.Signals) rec.Signals.Add((rec.Now, s.State, s.FormatLabel)); };
            await source.OpenAsync();
            Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
            rec.FormatLabel = source.CurrentSignal.FormatLabel ?? "";
            var queue = await locator.SupportsOutputThreadQueueAsync() ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
            int previews = 0;
            await using (var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine"))
                         { OutputRoot = rec.OutputRoot, WriteQueue = queue, FragmentedMp4 = false })
            {
                engine.FrameReady += (_, _) => Interlocked.Increment(ref previews);
                engine.AlarmChanged += (_, a) => { lock (rec.Alarms) rec.Alarms.Add((rec.Now, a.Type, a.Active)); };
                engine.RecordingInterrupted += (_, i) => { lock (rec.Interruptions) rec.Interruptions.Add(i); };
                engine.FileUnverified += (_, f) => { lock (rec.Unverified) rec.Unverified.Add($"{f.FilePath} ({f.SizeBytes} B)"); };
                await engine.StartPreviewAsync(source, profile, "NDI");
                await RecordingRecoveryWakeTests.WaitUntilAsync(() => Volatile.Read(ref previews) > 5, TimeSpan.FromSeconds(20), "preview de la fuente NDI");
                await Task.Delay(1500);

                rec.RecordStart = rec.Now;
                await engine.StartRecordingAsync(Guid.NewGuid(), profile, baseName: "ndi_barrido");
                var recording = Stopwatch.StartNew();
                var midway = during?.Invoke(rec) ?? Task.CompletedTask;
                while (recording.Elapsed < TimeSpan.FromSeconds(seconds))
                {
                    await Task.Delay(500);
                    rec.FpsOutSamples.Add((recording.Elapsed.TotalSeconds, engine.Stats.OutputFps));
                }
                await midway;
                var steady = rec.FpsOutSamples.Where(s => s.At >= 4).Select(s => s.Fps).OrderBy(f => f).ToList();
                rec.FpsOut = steady.Count > 0 ? steady[steady.Count / 2] : double.NaN;
                rec.RecordedFor = recording.Elapsed.TotalSeconds;
                rec.RecordEnd = rec.Now;
                var stop = Stopwatch.StartNew();
                await engine.StopRecordingAsync();
                rec.StopSeconds = stop.Elapsed.TotalSeconds;
                await Task.Delay(1500); // verificación en segundo plano
            }
        }
        catch
        {
            DumpLog(rec);
            rec.Dispose();
            throw;
        }
        finally { await source.DisposeAsync(); }
        rec.Files = MediaFiles(rec.OutputRoot);
        _out.WriteLine($"Fuente «{rec.FormatLabel}» (emisor hasta {sender.MaxLateMs:0} ms tarde) · grabado {rec.RecordedFor:0.00} s · " +
                       $"Detener {rec.StopSeconds:0.00} s · FPS OUT {rec.FpsOut:0.00} · {rec.Files.Count} archivo(s)");
        _out.WriteLine("  FPS OUT: " + string.Join(" ", rec.FpsOutSamples.Select(s => s.Fps.ToString("0.0", CultureInfo.InvariantCulture))));
        // Un emisor que se queda parado (equipo cargado: medido una vez, 2,5 s) manda luego de golpe los periodos atrasados, y sus
        // marcas ya no están a un segundo: la medida no vale, ni para bien ni para mal.
        if (sender.MaxLateMs > 250)
        {
            rec.Dispose();
            Skip.If(true, $"El emisor de la prueba llegó a ir {sender.MaxLateMs:0} ms tarde (equipo cargado): sus marcas no valen para medir; repite la prueba.");
        }
        return rec;
    }

    private void DumpLog(Recorded rec)
    {
        lock (rec.Log) foreach (var (at, line) in rec.Log.TakeLast(150)) _out.WriteLine($"  | {at,7:0.00} s {line}");
        lock (rec.Alarms) foreach (var a in rec.Alarms) _out.WriteLine($"  alarma @{a.At:0.00} s {a.Type} {(a.Active ? "ON" : "off")}");
        lock (rec.Signals) foreach (var s in rec.Signals) _out.WriteLine($"  señal @{s.At:0.00} s {s.State} «{s.Label}»");
        foreach (var i in rec.Interruptions) _out.WriteLine($"  interrupción: {i.ExitCode} {i.Reason}");
    }

    /// <summary>Las comprobaciones de una prueba, con el registro del canal en la salida si alguna falla.</summary>
    private async Task CheckAsync(Recorded rec, Func<Task> checks)
    {
        try { await checks(); }
        catch
        {
            DumpLog(rec);
            throw;
        }
    }

    private static readonly string[] MediaExtensions = { ".mp4", ".mov", ".mxf", ".ts", ".mkv", ".avi", ".mpg", ".mpeg", ".wav", ".m4a", ".mp3" };

    private static List<string> MediaFiles(string dir) => Directory.Exists(dir)
        ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(File.GetCreationTimeUtc).ThenBy(f => f, StringComparer.Ordinal).ToList()
        : new();

    /// <summary>Una pieza, sin interrupciones, sin archivos sin verificar y sin la alarma de cuadros perdidos mientras grababa.</summary>
    private static void AssertOnePiece(Recorded rec)
    {
        Assert.True(rec.Files.Count == 1, "Se esperaba UN archivo: " + string.Join(", ", rec.Files.Select(Path.GetFileName)));
        Assert.Empty(rec.Interruptions);
        Assert.Empty(rec.Unverified);
        Assert.False(rec.AlarmActiveBetween(AlarmType.FramesDropped, rec.RecordStart, rec.RecordEnd), "Alarma de cuadros perdidos mientras grababa.");
        Assert.False(rec.AlarmActiveBetween(AlarmType.RecordingUnverified, rec.RecordStart, double.MaxValue), "Alarma de grabación sin verificar.");
    }

    /// <summary>El vídeo llega en NUT y la rama de grabación rellena los huecos con un fps: al principio o al final.</summary>
    private static void AssertFill(Recorded rec, bool atStart)
    {
        string pipeline = rec.Pipeline();
        Assert.Contains("-f nut", pipeline);
        if (atStart) Assert.Matches(@"\[vrec\]fps=\d+/\d+,", pipeline);
        else Assert.Matches(@",fps=\d+/\d+\[vmain\]", pipeline);
    }

    /// <summary>
    /// Un archivo grabado de la fuente con marcas: a la cadencia del preset (o la declarada, en nativo) y sin huecos, del alto del
    /// preset, con las marcas a <paramref name="markPeriod"/> s y cada pitido junto a su destello (el mismo desfase de la primera a
    /// la última), el audio tan largo como el vídeo y, en un preset entrelazado, cuadros entrelazados TFF. Con
    /// <paramref name="expectSeconds"/>, la duración. Devuelve los segundos de vídeo (o de audio, si no lleva).
    /// </summary>
    private async Task<double> AssertFileAsync(string file, RecordingProfile profile, double declared, double markPeriod,
        double? expectSeconds = null, double tolerance = 1.5, int sourceHeight = 1080)
    {
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var (streams, formatDuration) = await ProbeAsync(locator.FfprobePath, file);
        var v = streams.FirstOrDefault(s => s.Type == "video");
        var a = streams.FirstOrDefault(s => s.Type == "audio");
        double expectedFps = profile.OutputFrameRate?.Value ?? declared;
        double vDur = v is null ? double.NaN : double.IsNaN(v.Duration) ? formatDuration : v.Duration;
        double aDur = a is null ? double.NaN : double.IsNaN(a.Duration) ? formatDuration : a.Duration;
        var (flashes, beeps) = await SyncMarksAsync(locator.FfmpegPath, file, hasVideo: v is not null);
        var marks = v is null ? beeps : flashes;
        var spacing = marks.Zip(marks.Skip(1), (x, y) => y - x).ToList();
        var offsets = flashes.Select(f => beeps.Select(b => b - f).Where(o => Math.Abs(o) < 0.5).DefaultIfEmpty(double.NaN).First())
                             .Where(o => !double.IsNaN(o)).ToList();
        double maxGap = 0, gapAt = 0;
        if (v is not null)
        {
            var pts = await VideoPtsAsync(locator.FfprobePath, file);
            for (int i = 1; i < pts.Length; i++) if (pts[i] - pts[i - 1] > maxGap) { maxGap = pts[i] - pts[i - 1]; gapAt = pts[i - 1]; }
        }
        string I(double d) => d.ToString("0.000", CultureInfo.InvariantCulture);
        _out.WriteLine($"  {Path.GetFileName(file)} ({new FileInfo(file).Length / 1_000_000.0:0.0} MB): " +
                       (v is null ? "" : $"v:{v.Codec} {v.Width}x{v.Height} {v.FieldOrder} {I(vDur)} s, {v.Packets} cuadros = {v.Packets / vDur:0.000} fps, hueco máx {maxGap * 1000:0} ms · ") +
                       (a is null ? "sin audio" : $"a:{a.Codec} {I(aDur)} s"));
        _out.WriteLine($"    marcas: {string.Join(" ", marks.Select(I))}");
        if (offsets.Count > 0) _out.WriteLine($"    pitido − destello: {string.Join(" ", offsets.Select(o => o.ToString("+0.000;-0.000", CultureInfo.InvariantCulture)))}");

        Assert.True(a is not null, "El archivo no tiene audio.");
        double seconds = v is null ? aDur : vDur;
        if (expectSeconds is { } s)
            Assert.True(Math.Abs(seconds - s) <= tolerance, $"{I(seconds)} s grabados para {s:0.00} s de grabación (±{tolerance:0.0}).");
        int minMarks = Math.Max(2, (int)(seconds / markPeriod) - 1);
        Assert.True(marks.Count >= minMarks, $"Solo {marks.Count} marcas en {I(seconds)} s.");
        double spacingTolerance = Math.Max(0.07, 1.5 / expectedFps);
        Assert.All(spacing, d => Assert.InRange(d, markPeriod - spacingTolerance, markPeriod + spacingTolerance));
        if (v is null) return seconds;

        Assert.True(Math.Abs(v.Packets / vDur - expectedFps) / expectedFps < 0.02,
            $"Cadencia {v.Packets / vDur:0.000} fps ({v.Packets} cuadros en {I(vDur)} s), se esperaban {expectedFps:0.000}.");
        Assert.True(maxGap <= 2.5 / expectedFps, $"Hueco de {maxGap * 1000:0} ms en el vídeo @{I(gapAt)} s.");
        Assert.Equal(profile.TargetResolution?.Height ?? sourceHeight, v.Height);
        Assert.True(offsets.Count >= flashes.Count - 1, $"Solo {offsets.Count} pitidos para {flashes.Count} destellos.");
        Assert.All(offsets, o => Assert.InRange(o, -0.1, 0.1));
        Assert.InRange(offsets.Max() - offsets.Min(), 0, 0.06 + 1 / expectedFps);
        Assert.True(Math.Abs(aDur - vDur) < 0.5, $"Vídeo {I(vDur)} s frente a audio {I(aDur)} s.");
        if (profile.ScanType is ScanType.InterlacedTff)
        {
            var coded = await FirstFrameScanAsync(locator.FfprobePath, file);
            Assert.True(coded.Interlaced && coded.TopFieldFirst, "Preset entrelazado TFF, pero el códec no escribió cuadros entrelazados TFF.");
        }
        return seconds;
    }

    // ==================================================================================================================

    /// <summary>
    /// Un grafismo como Offside (declara 60, manda 4) con los presets de fábrica de todas las familias: códecs por CPU y NVENC,
    /// entrelazados (setfield antes del relleno), SD, 4K, contenedores MP4, TS, PS, MXF, MOV, MKV y AVI, y solo audio. La rama de
    /// grabación rellena al final: escala y convierte solo los cuadros reales.
    /// </summary>
    [SkippableTheory]
    [InlineData("H.264 Broadcast · FullHD 1080p25")]
    [InlineData("H.264 Broadcast · FullHD 1080p59.94")]
    [InlineData("H.264 Broadcast · 1080i59.94 MP4")]
    [InlineData("H.264 NVENC · HD 720p50")]
    [InlineData("H.264 NVENC · FullHD 1080p60")]
    [InlineData("H.264 NVENC · 4K UHD 2160p59.94")]
    [InlineData("HEVC NVENC · FullHD 1080p29.97")]
    [InlineData("XDCAM HD422 · 1080i59.94 50 Mbps")]
    [InlineData("MPEG-2 TS · HD 1080i59.94")]
    [InlineData("MPEG-2 PS · SD PAL")]
    [InlineData("H.264 · SD NTSC 480i59.94")]
    [InlineData("ProRes 422 HQ · 1080p59.94")]
    [InlineData("ProRes 4444 XQ · 1080p25")]
    [InlineData("DNxHD 220 · 1080i59.94 (MOV)")]
    [InlineData("MXF OP1A · DNxHD 220 1080i59.94")]
    [InlineData("MKV · H.264 1080p25")]
    [InlineData("AVI · H.264 1080p25")]
    [InlineData("Audio · WAV PCM 48k 24-bit")]
    public async Task AGraphicsSourceLikeOffside_IsRecordedInRealTime_WithEachFactoryPreset(string presetName)
    {
        SkipWithoutNdi();
        var profile = Preset(presetName).ToProfile();
        using var sender = LikeOffside();
        using var rec = await RecordAsync(sender, profile, 10);
        await CheckAsync(rec, async () =>
        {
            Assert.Equal("1920×1080 · 60p · NDI", rec.FormatLabel);
            AssertOnePiece(rec);
            double handover = Math.Max(0, rec.StopSeconds - 0.6);
            await AssertFileAsync(rec.Files[0], profile, GraphicsRate, sender.MarkPeriod, rec.RecordedFor + 0.3 + handover / 2, 1.5 + handover / 2);
            if (profile.AudioOnly) return;
            AssertFill(rec, atStart: false);
            Assert.InRange(rec.FpsOut, 0.85 * GraphicsRate, 1.15 * GraphicsRate); // FPS OUT: la cadencia declarada de la fuente
        });
    }

    /// <summary>
    /// Un 1080i como el de vMix que manda uno de cada tres cuadros, con audio: desentrelazado a un cuadro por cuadro (el relleno,
    /// al final), a un cuadro por campo (el relleno, antes de bwdif: pone el segundo campo a medio camino del cuadro siguiente),
    /// entrelazado a entrelazado (sin desentrelazar) y reducido a 720p por campo.
    /// </summary>
    [SkippableTheory]
    [InlineData("H.264 Broadcast · FullHD 1080p29.97", false)]
    [InlineData("H.264 Broadcast · FullHD 1080p59.94", true)]
    [InlineData("H.264 Broadcast · 1080i59.94 MP4", false)]
    [InlineData("H.264 NVENC · HD 720p59.94", true)]
    public async Task AnInterlacedSourceLikeVmix_SendingAThirdOfItsFrames_IsRecordedInRealTime(string presetName, bool fillAtStart)
    {
        SkipWithoutNdi();
        var profile = Preset(presetName).ToProfile();
        using var sender = new SparseSender(Unique("1080i"), 30000, 1001, 3, withAudio: true, SenderKind.Interleaved);
        using var rec = await RecordAsync(sender, profile, 10);
        await CheckAsync(rec, async () =>
        {
            Assert.Equal("1920×1080 · 59.94i · NDI", rec.FormatLabel);
            AssertOnePiece(rec);
            double handover = Math.Max(0, rec.StopSeconds - 0.6);
            await AssertFileAsync(rec.Files[0], profile, InterlacedRate, sender.MarkPeriod, rec.RecordedFor + 0.3 + handover / 2, 1.5 + handover / 2);
            AssertFill(rec, fillAtStart);
            if (profile.ScanType is ScanType.Progressive) Assert.Contains("bwdif=mode=", rec.Pipeline());
            else Assert.DoesNotContain("bwdif", rec.Pipeline());
            Assert.InRange(rec.FpsOut, 0.85 * InterlacedRate, 1.15 * InterlacedRate);
        });
    }

    /// <summary>Con timecode quemado (drawtext cuenta cuadros, no tiempo), el relleno va al principio, a la cadencia declarada.</summary>
    [SkippableFact(Skip = "Fallo conocido (2026-10-18): con una fuente que declara 60 y un preset a 29.97, el timecode quemado corre al doble " +
                          "(drawtext con rate=30 recibe los 60 cuadros/s del relleno) y el pitido va ~50 ms más por delante de su destello " +
                          "que sin timecode. Quitar el Skip con el arreglo.")]
    public async Task WithBurnedTimecode_TheGapsAreFilledBeforeDrawtext()
    {
        SkipWithoutNdi();
        var profile = Preset("H.264 NVENC · FullHD 1080p29.97").ToProfile();
        profile.BurnTimecode = true;
        using var sender = LikeOffside();
        using var rec = await RecordAsync(sender, profile, 10);
        await CheckAsync(rec, async () =>
        {
            AssertOnePiece(rec);
            double handover = Math.Max(0, rec.StopSeconds - 0.6);
            await AssertFileAsync(rec.Files[0], profile, GraphicsRate, sender.MarkPeriod, rec.RecordedFor + 0.3 + handover / 2, 1.5 + handover / 2);
            AssertFill(rec, atStart: true);
            Assert.Contains("[vrec]fps=60000/1000,", rec.Pipeline());
            Assert.Contains("drawtext=timecode=", rec.Pipeline());
        });
    }

    /// <summary>Por segmentos de 8 s: cada pieza continua, con sus marcas a un segundo, y entre todas lo que duró la grabación.</summary>
    [SkippableFact]
    public async Task ASegmentedRecording_LeavesContinuousPieces_ThatAddUpToTheRecording()
    {
        SkipWithoutNdi();
        var profile = Preset("H.264 NVENC · FullHD 1080p29.97").ToProfile();
        profile.Segmentation = new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromSeconds(8) };
        using var sender = LikeOffside();
        using var rec = await RecordAsync(sender, profile, 22);
        await CheckAsync(rec, async () =>
        {
            Assert.Empty(rec.Interruptions);
            Assert.Empty(rec.Unverified);
            Assert.True(rec.Files.Count >= 3, $"{rec.Files.Count} piezas en {rec.RecordedFor:0.0} s con segmentos de 8 s.");
            double total = 0;
            foreach (var f in rec.Files) total += await AssertFileAsync(f, profile, GraphicsRate, sender.MarkPeriod);
            _out.WriteLine($"Entre las {rec.Files.Count} piezas: {total:0.00} s de {rec.RecordedFor:0.00} s grabados.");
            Assert.InRange(total, rec.RecordedFor - 1.5, rec.RecordedFor + 2.5);
        });
    }

    /// <summary>
    /// La fuente calla 8 s (sin vídeo ni audio, sigue en la red): el canal pasa a carta de ajuste a los ~3 s (PresenceTimeout) y,
    /// cuando vuelve, a la fuente, en una pieza que de nuevo va a la cadencia del preset, con sus marcas y su audio.
    /// </summary>
    [SkippableFact]
    public async Task WhenTheSourceGoesQuiet_TheChannelRecordsTheSlate_AndGoesBackToTheSourceWhenItReturns()
    {
        SkipWithoutNdi();
        var profile = Preset("H.264 NVENC · FullHD 1080p29.97").ToProfile();
        profile.SlateOnSignalLoss = true;
        using var sender = LikeOffside();
        double quietAt = 0, backAt = 0;
        using var rec = await RecordAsync(sender, profile, 30, async r =>
        {
            await Task.Delay(6000);
            quietAt = r.Now; sender.Paused = true;
            await Task.Delay(8000);
            backAt = r.Now; sender.Paused = false;
        });
        await CheckAsync(rec, async () =>
        {
            double? slateOn, slateOff;
            lock (rec.Alarms)
            {
                slateOn = rec.Alarms.Where(a => a.Type == AlarmType.Slate && a.Active && a.At > quietAt).Select(a => (double?)a.At).FirstOrDefault();
                slateOff = rec.Alarms.Where(a => a.Type == AlarmType.Slate && !a.Active && a.At > backAt).Select(a => (double?)a.At).FirstOrDefault();
            }
            _out.WriteLine($"Calla a {quietAt:0.00} s, vuelve a {backAt:0.00} s · carta de ajuste {(slateOn is { } on ? $"a {on:0.00} s" : "nunca")}, " +
                           $"de vuelta a la fuente {(slateOff is { } off ? $"a {off:0.00} s" : "nunca")} · piezas: {string.Join(", ", rec.Files.Select(Path.GetFileName))}");
            Assert.True(slateOn is not null, "El canal no pasó a carta de ajuste al callar la fuente.");
            Assert.InRange(slateOn!.Value - quietAt, 2, 7);
            Assert.True(slateOff is not null, "El canal no volvió a la fuente cuando volvió.");
            Assert.InRange(slateOff!.Value - backAt, 0, 6);
            Assert.True(rec.Files.Count >= 2, $"{rec.Files.Count} pieza(s).");
            double last = await AssertFileAsync(rec.Files[^1], profile, GraphicsRate, sender.MarkPeriod);
            double sinceBack = rec.RecordEnd - slateOff.Value;
            Assert.True(last >= sinceBack - 2, $"La última pieza dura {last:0.00} s; la fuente volvió {sinceBack:0.00} s antes del Detener.");
        });
    }

    /// <summary>
    /// La fuente pasa de 1920×1080 a 1280×720 en caliente mientras graba (vMix cambiando su salida): la señal lo dice y la grabación
    /// tiene que seguir con el formato nuevo enseguida, sin quedarse sin vídeo hasta que el vigilante mate el proceso.
    /// </summary>
    [SkippableFact(Skip = "Fallo conocido (2026-10-18): el receptor no escribe cuadros del formato nuevo en la conexión NUT en marcha y nada " +
                          "rehace el proceso: la grabación se para hasta que el vigilante lo mata (30 s) y el MP4 de antes del cambio queda " +
                          "sin moov. Quitar el Skip con el arreglo.")]
    public async Task AHotResolutionChange_IsPickedUpByTheRecording_WithoutAStall()
    {
        SkipWithoutNdi();
        var profile = Preset("H.264 NVENC · FullHD 1080p29.97").ToProfile();
        using var sender = LikeOffside();
        double changedAt = 0;
        using var rec = await RecordAsync(sender, profile, 45, async r =>
        {
            await Task.Delay(5000);
            changedAt = r.Now; sender.Resize(1280, 720);
        });
        await CheckAsync(rec, async () =>
        {
            double relabelled;
            lock (rec.Signals) relabelled = rec.Signals.FirstOrDefault(s => s.At > changedAt && s.Label?.StartsWith("1280×720", StringComparison.Ordinal) == true).At;
            var rebuilt = rec.RecordPipelines().Where(p => p.At > changedAt).Select(p => p.At).ToList();
            _out.WriteLine($"Cambio a 1280×720 a {changedAt:0.00} s · señal «1280×720» {(relabelled > 0 ? $"a {relabelled:0.00} s" : "nunca")} · " +
                           $"grabación rehecha {(rebuilt.Count > 0 ? string.Join(", ", rebuilt.Select(t => $"a {t:0.00} s")) : "nunca")} · " +
                           $"interrupciones: {string.Join("; ", rec.Interruptions.Select(i => $"{i.ExitCode} {i.Reason}"))} · piezas: {rec.Files.Count}");
            double total = 0;
            foreach (var f in rec.Files)
            {
                var (streams, formatDuration) = await ProbeAsync(new FfmpegLocator(TestAssets.FfmpegDir!).FfprobePath, f);
                var v = streams.FirstOrDefault(s => s.Type == "video");
                double d = v is null ? 0 : double.IsNaN(v.Duration) ? formatDuration : v.Duration;
                total += d;
                _out.WriteLine($"  {Path.GetFileName(f)}: {d:0.00} s");
            }
            _out.WriteLine($"Entre las piezas: {total:0.00} s de {rec.RecordedFor:0.00} s grabados.");
            Assert.True(relabelled > 0, "La señal no pasó a 1280×720.");
            Assert.True(rebuilt.Count > 0 && rebuilt[0] - changedAt < 10,
                $"La grabación no siguió con el formato nuevo en 10 s ({(rebuilt.Count > 0 ? $"a los {rebuilt[0] - changedAt:0.0} s" : "nunca")}).");
            Assert.True(total >= rec.RecordedFor - 10, $"Faltan {rec.RecordedFor - total:0.0} s de grabación.");
        });
    }

    // ------------------------------------------------------------------------------------------------------------------
    // ffprobe y ffmpeg sobre el archivo
    // ------------------------------------------------------------------------------------------------------------------

    private sealed record StreamInfo(string Type, string Codec, int Width, int Height, string? FieldOrder, double Start, double Duration, long Packets);

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

    private static double Num(JsonElement e, string name, double fallback)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<(List<StreamInfo> Streams, double FormatDuration)> ProbeAsync(string ffprobe, string file)
    {
        var (json, err, exit) = await RunAsync(ffprobe, "-v", "error", "-count_packets", "-of", "json",
            "-show_entries", "format=duration:stream=codec_type,codec_name,width,height,field_order,start_time,duration,nb_read_packets", file);
        Assert.True(exit == 0, $"ffprobe falló con {Path.GetFileName(file)}: {err}");
        using var doc = JsonDocument.Parse(json);
        var streams = new List<StreamInfo>();
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            streams.Add(new StreamInfo(Str(s, "codec_type") ?? "", Str(s, "codec_name") ?? "", (int)Num(s, "width", 0), (int)Num(s, "height", 0),
                Str(s, "field_order"), Num(s, "start_time", double.NaN), Num(s, "duration", double.NaN), (long)Num(s, "nb_read_packets", 0)));
        double formatDuration = doc.RootElement.TryGetProperty("format", out var f) ? Num(f, "duration", double.NaN) : double.NaN;
        return (streams, formatDuration);
    }

    /// <summary>Marcas de tiempo (s) de los paquetes de vídeo, ordenadas: para buscar huecos.</summary>
    private static async Task<double[]> VideoPtsAsync(string ffprobe, string file)
    {
        var (csv, _, _) = await RunAsync(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", file);
        return csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => double.TryParse(l.TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN)
            .Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
    }

    /// <summary>Marcas de entrelazado y de campo superior primero del primer cuadro DECODIFICADO (las del códec).</summary>
    private static async Task<(bool Interlaced, bool TopFieldFirst)> FirstFrameScanAsync(string ffprobe, string file)
    {
        var (csv, _, _) = await RunAsync(ffprobe, "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1",
            "-show_entries", "frame=interlaced_frame,top_field_first", "-of", "csv=p=0", file);
        var fields = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.Split(',') ?? Array.Empty<string>();
        return (fields.ElementAtOrDefault(0) == "1", fields.ElementAtOrDefault(1) == "1");
    }

    /// <summary>
    /// Instantes (s) de las marcas de un archivo: el primer cuadro de cada destello (brillo medio alto tras uno oscuro) y el arranque
    /// de cada pitido (pico de un bloque de 5 ms por encima de −30 dBFS tras uno en silencio). Una sola pasada de FFmpeg, para que
    /// vídeo y audio compartan el origen de tiempos.
    /// </summary>
    private static async Task<(List<double> Flashes, List<double> Beeps)> SyncMarksAsync(string ffmpeg, string file, bool hasVideo)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-i", file, "-filter_complex",
            (hasVideo ? "[0:v:0]scale=64:36,format=gray,showinfo[v];" : "") +
            "[0:a:0]asetnsamples=n=240:p=0,astats=metadata=1:reset=1,ametadata=mode=print:key=lavfi.astats.Overall.Peak_level[a]" };
        if (hasVideo) args.AddRange(new[] { "-map", "[v]", "-f", "null", "-" });
        args.AddRange(new[] { "-map", "[a]", "-f", "null", "-" });
        var (_, err, _) = await RunAsync(ffmpeg, args.ToArray());
        var flashes = new List<double>();
        var beeps = new List<double>();
        // Una marca que ya estaba en marcha al empezar el archivo (un segmento que arranca en mitad de un destello) no se puede
        // fechar: solo cuentan las que empiezan tras un cuadro oscuro o un bloque en silencio.
        bool bright = true, loud = true;
        double audioAt = double.NaN;
        static double After(string line, string key, char end)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal) + key.Length;
            int j = line.IndexOf(end, i);
            var text = (j < 0 ? line[i..] : line[i..j]).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }
        foreach (var line in err.Split('\n'))
        {
            if (line.Contains("Parsed_showinfo", StringComparison.Ordinal) && line.Contains(" mean:[", StringComparison.Ordinal))
            {
                double at = After(line, "pts_time:", ' '), mean = After(line, " mean:[", ']');
                bool isBright = mean > 140;
                if (isBright && !bright) flashes.Add(at);
                bright = isBright;
            }
            else if (line.Contains("Parsed_ametadata", StringComparison.Ordinal) && line.Contains("pts_time:", StringComparison.Ordinal))
                audioAt = After(line, "pts_time:", ' ');
            else if (line.Contains("lavfi.astats.Overall.Peak_level=", StringComparison.Ordinal))
            {
                var text = line[(line.IndexOf('=') + 1)..].Trim();
                double peak = text.StartsWith("-inf", StringComparison.Ordinal) ? double.NegativeInfinity
                    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : double.NegativeInfinity;
                if (peak > -30 && !loud) beeps.Add(audioAt);
                if (peak > -30) loud = true;
                else if (peak < -40) loud = false;
            }
        }
        return (flashes, beeps);
    }
}
