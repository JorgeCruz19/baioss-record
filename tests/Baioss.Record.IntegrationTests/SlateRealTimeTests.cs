using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Carta de ajuste a tiempo real, con FFmpeg real. Al perder la señal grabando, el motor sustituye la fuente por barras SMPTE +
/// silencio generados con lavfi (<see cref="FfmpegArgumentBuilder.BuildSlate"/>). Un generador lavfi sin «-re» produce tan
/// deprisa como codifica la máquina (medido con el FFmpeg empaquetado: ~14× el tiempo real con x264 a 720p25): una caída de
/// señal de minutos dejaba HORAS de barras en el disco, y la pieza duraba mucho más que el hueco que rellenaba. Aquí se comprueba
/// que X s de carta de ajuste son X s de progreso y de archivo, que audio y vídeo acaban juntos (con un generador por entrada el
/// audio acababa 0,6 s antes), que el relevo y la sonda de recuperación siguen funcionando, y que las piezas
/// fuente → barras → fuente tienen el mismo formato y se unen en una sola.
/// </summary>
public sealed class SlateRealTimeTests
{
    private readonly ITestOutputHelper _out;
    public SlateRealTimeTests(ITestOutputHelper output) => _out = output;

    /// <summary>Cómo se entera el motor de la caída y de la vuelta de la señal (ver <see cref="SwitchableSignalSource"/>).</summary>
    public enum InputKind { Device, Relay, RelayWithPreroll }

    /// <summary>Tiempo sin señal que se rellena con barras.</summary>
    private static readonly TimeSpan Outage = TimeSpan.FromSeconds(8);

    /// <summary>Como un preset de fábrica: formato fijo (720p25), x264, AAC estéreo en MP4, con carta de ajuste.</summary>
    private static RecordingProfile Profile() => new()
    {
        Name = "carta", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(2), GopSize = 50,
        TargetResolution = Resolution.Hd720, OutputFrameRate = FrameRate.P25,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
        SlateOnSignalLoss = true,
    };

    [SkippableTheory]
    [InlineData(2, null, 0)]   // estéreo, sin cola de escritura (FFmpeg anterior al 6)
    [InlineData(2, null, 10)]  // estéreo con la cola de escritura de producción (Recording:WriteQueueSeconds)
    [InlineData(8, "1,3", 10)] // audio embebido multicanal: silencio de 8 canales por los mismos pans que la grabación (2 pistas)
    public async Task TheSlateArgv_AdvancesWithTheWallClock_AndItsFileLastsWhatItRan(int channels, string? pairs, int writeQueueSeconds)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDirectory("argv");
        var source = new SwitchableSignalSource(InputKind.Device, channels, pairs);
        await source.OpenAsync();
        var preview = new PreviewDrain();
        FfmpegProcessSupervisor? running = null;
        try
        {
            // El argv tal cual lo construye el motor al entrar en carta de ajuste grabando (mismo preview de 640×360).
            var builder = new FfmpegArgumentBuilder().From(source).Using(Profile()).ForChannel("SLT").ToDirectory(dir)
                .WithPreviewSink($"tcp://127.0.0.1:{preview.Port}").WithWriteQueue(TimeSpan.FromSeconds(writeQueueSeconds));
            var args = builder.BuildSlate(recording: true, 640, 360);
            string file = builder.OutputFilePath;
            _out.WriteLine("argv: " + string.Join(' ', args));

            var samples = new List<(double Wall, double Media, string Speed)>();
            var block = new Dictionary<string, string>();
            var clock = new Stopwatch();
            running = new FfmpegProcessSupervisor(locator.FfmpegPath, NullLogger.Instance) { RestartInternally = false };
            running.ProgressLine += (_, line) =>
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) return;
                string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                lock (samples)
                {
                    if (key != "progress") { block[key] = value; return; }
                    if (block.TryGetValue("out_time_us", out var us) && long.TryParse(us, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
                        samples.Add((clock.Elapsed.TotalSeconds, t / 1e6, block.GetValueOrDefault("speed", "?")));
                    block.Clear();
                }
            };
            clock.Start();
            await running.StartAsync(args);
            await Task.Delay(Outage);
            double ran = clock.Elapsed.TotalSeconds; // se pide el cierre
            var stopping = running;
            running = null;
            await stopping.DisposeAsync(); // «q»: FFmpeg cierra el archivo, como al salir de la carta de ajuste
            double lived = clock.Elapsed.TotalSeconds; // el proceso ya salió (atender la «q» tarda hasta ~1 s)

            List<(double Wall, double Media, string Speed)> s;
            lock (samples) s = samples.ToList();
            foreach (var x in s) _out.WriteLine(FormattableString.Invariant($"reloj {x.Wall,5:0.0} s · out_time {x.Media,6:0.0} s · speed {x.Speed}"));
            Assert.True(s.Count >= 4, $"pocas muestras de progreso: {s.Count}");

            // 1) El progreso no va por delante del reloj (como mucho el empujón inicial de -re, 0,5 s, y algo de margen)…
            var last = s[^1];
            Assert.True(last.Media <= last.Wall + 1.0,
                FormattableString.Invariant($"la carta de ajuste va por delante del reloj: {last.Media:0.0} s de barras en {last.Wall:0.0} s"));
            // …y en régimen avanza un segundo por segundo (barras a 720p25: la máquina va sobrada).
            var steady = s.Where(x => x.Wall >= 2).ToList();
            Assert.True(steady.Count >= 3, $"pocas muestras en régimen: {steady.Count}");
            double rate = (steady[^1].Media - steady[0].Media) / (steady[^1].Wall - steady[0].Wall);
            Assert.InRange(rate, 0.8, 1.2);

            // 2) El archivo dura lo que estuvo en marcha (como mucho su vida más el empujón inicial de -re, 0,5 s), con audio y
            //    vídeo alineados de principio a fin.
            var info = await ProbeAsync(locator.FfprobePath, file);
            _out.WriteLine(FormattableString.Invariant($"en marcha {ran:0.0} s (salió a los {lived:0.0} s) → archivo {info.Duration:0.00} s ({info.Format}); vídeo {info.Video.Start:0.000}+{info.Video.Duration:0.00} s, audio {info.Audio.Start:0.000}+{info.Audio.Duration:0.00} s"));
            Assert.InRange(info.Duration, ran - 2.0, lived + 0.6);
            Assert.Equal(pairs is null ? 1 : 2, info.Streams.Count(x => x.Type == "audio")); // las pistas de la grabación real
            AssertAligned(info);
        }
        finally
        {
            if (running is not null) await running.DisposeAsync();
            await preview.DisposeAsync();
            TryDelete(dir);
        }
    }

    [SkippableTheory]
    [InlineData(InputKind.Device)]
    [InlineData(InputKind.Relay)]
    [InlineData(InputKind.RelayWithPreroll)]
    public async Task ASignalLoss_IsFilledWithBarsInRealTime_AndThePiecesJoinIntoOne(InputKind kind)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDirectory(kind.ToString().ToLowerInvariant());
        var clock = Stopwatch.StartNew();
        var log = new ConcurrentQueue<string>();
        var logger = new DecklinkRelayContinuityTests.CaptureLogger(l => log.Enqueue(FormattableString.Invariant($"{clock.Elapsed.TotalSeconds,6:0.00} {l}")));
        var source = new SwitchableSignalSource(kind);
        await source.OpenAsync();
        try
        {
            List<string> files;
            double window, recordWall, slateMedia = 0;
            // Con la cola de escritura de producción (Recording:WriteQueueSeconds = 10).
            await using (var engine = new FfmpegChannelEngine(locator, logger) { OutputRoot = dir, WriteQueue = TimeSpan.FromSeconds(10) })
            {
                int frames = 0;
                engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);
                // El out_time más alto que dio el progreso mientras el canal estaba en carta de ajuste.
                var gate = new object();
                engine.StatsUpdated += (_, st) => { if (engine.IsSlate) lock (gate) slateMedia = Math.Max(slateMedia, Seconds(st.Timecode)); };

                await engine.StartPreviewAsync(source, Profile(), "SLT");
                await WaitUntilAsync(() => Volatile.Read(ref frames) >= 25, TimeSpan.FromSeconds(20), "preview en marcha");

                var recordStart = clock.Elapsed;
                await engine.StartRecordingAsync(Guid.NewGuid(), Profile());
                await Task.Delay(TimeSpan.FromSeconds(3)); // la fuente real

                // Se va la señal. Una entrada con relé lo avisa y el motor pasa a barras con su proceso aún en marcha; un
                // dispositivo directo no avisa: su proceso cae (aquí, se mata) y la recuperación ve que no hay señal.
                source.LoseSignal();
                if (kind == InputKind.Device) Assert.True(engine.KillRecorderProcessForTest(), "no había proceso de grabación que matar");
                await WaitUntilAsync(() => engine.IsSlate, TimeSpan.FromSeconds(20), "carta de ajuste");
                var slateOn = clock.Elapsed;
                await Task.Delay(Outage);
                source.RestoreSignal();
                await WaitUntilAsync(() => !engine.IsSlate, TimeSpan.FromSeconds(30), "vuelta a la fuente");
                window = (clock.Elapsed - slateOn).TotalSeconds;
                Assert.Equal(RecordingState.Recording, engine.State);
                await Task.Delay(TimeSpan.FromSeconds(3)); // la fuente real otra vez

                await engine.StopRecordingAsync();
                recordWall = (clock.Elapsed - recordStart).TotalSeconds;
                // Ponerles nombre al detener, como el operador, espera además a que termine la optimización (remux) de cada pieza.
                files = (await engine.RenameSessionFilesAsync("Carta")).Select(p => p.New).ToList();
            }

            // La sonda de recuperación: solo se sondea un dispositivo; una entrada que avisa de su señal no se sondea nunca.
            if (kind == InputKind.Device) Assert.True(source.Probes >= 1, "el motor no sondeó el dispositivo para salir de la carta de ajuste");
            else Assert.Equal(0, source.Probes);

            Assert.Equal(3, files.Count); // fuente · barras · fuente
            var infos = new List<MediaInfo>();
            foreach (var f in files) infos.Add(await ProbeAsync(locator.FfprobePath, f));
            for (int i = 0; i < files.Count; i++)
                _out.WriteLine(FormattableString.Invariant($"{Path.GetFileName(files[i])}: {infos[i].Duration:0.00} s · {infos[i].Format}"));
            double total = infos.Sum(i => i.Duration);
            _out.WriteLine(FormattableString.Invariant($"carta de ajuste {window:0.0} s (progreso hasta {slateMedia:0.0} s); grabación {recordWall:0.0} s → piezas {total:0.0} s"));

            // 1) X s de carta de ajuste = X s de barras en el archivo (y en el progreso): sin -re eran unas 14 veces más. Como
            //    mucho la ventana más el empujón inicial de -re (0,5 s); por debajo, lo que tardan en arrancar y relevarse.
            var slate = infos[1];
            Assert.InRange(slate.Duration, window - 4.0, window + 1.5);
            Assert.True(slateMedia <= window + 1.5,
                FormattableString.Invariant($"el progreso de la carta de ajuste llegó a {slateMedia:0.0} s en {window:0.0} s"));
            AssertAligned(slate);
            // 2) Lo grabado ocupa lo que duró la grabación: los huecos de una caída restan unos segundos, y suman los solapes del
            //    relevo (el saliente graba hasta que el entrante toma el preview y atiende su «q»: 1–2 s por cambio, más con
            //    asentamiento en una máquina cargada) y el empujón inicial de -re de cada proceso (0,5 s). Sin -re eran +100 s.
            Assert.InRange(total, recordWall - 7.0, recordWall + 10.0);
            // 3) Mismo formato en las tres piezas: se unen sin recodificar y la unión se decodifica sin errores. Con el
            //    dispositivo, la primera pieza la cortó la caída simulada (proceso matado): su último paquete H.264 queda a
            //    medias (ffprobe: «Invalid NAL unit size») y la unión se detendría ahí, así que se une barras → fuente.
            Assert.All(infos, i => Assert.Equal(infos[0].Format, i.Format));
            int first = kind == InputKind.Device ? 1 : 0;
            var joinable = files.Skip(first).ToList();
            double joinableTotal = infos.Skip(first).Sum(i => i.Duration);
            var joined = Path.Combine(dir, "unidas.mp4");
            var list = Path.Combine(dir, "piezas.txt");
            await File.WriteAllLinesAsync(list, joinable.Select(f => $"file '{f.Replace('\\', '/').Replace("'", "'\\''")}'"));
            var concat = await RunAsync(locator.FfmpegPath, "-hide_banner", "-v", "error", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-y", joined);
            Assert.True(concat.Exit == 0 && concat.Err.Length == 0, "no se pudieron unir las piezas: " + concat.Err);
            var union = await ProbeAsync(locator.FfprobePath, joined);
            Assert.InRange(union.Duration, joinableTotal - 1.5, joinableTotal + 1.5);
            var decode = await RunAsync(locator.FfmpegPath, "-hide_banner", "-v", "error", "-i", joined, "-f", "null", "-");
            Assert.True(decode.Exit == 0 && decode.Err.Length == 0, "la unión no se decodifica limpia: " + decode.Err);
        }
        finally
        {
            foreach (var line in log.Where(l => !l.Contains("[Debug]", StringComparison.Ordinal))) _out.WriteLine(line);
            TryDelete(dir);
        }
    }

    // --- Utilidades ---

    /// <summary>Cada pista de audio empieza con el vídeo y acaba con él, como en las piezas de la fuente real (medido: el audio
    /// acaba 0,07–0,09 s después, lo que dura un bloque de AAC). Con un generador por entrada acababa 0,6 s antes.</summary>
    private static void AssertAligned(MediaInfo info)
    {
        foreach (var audio in info.Streams.Where(s => s.Type == "audio"))
        {
            Assert.True(info.Video.Start <= 0.1 && audio.Start <= 0.1,
                FormattableString.Invariant($"no empiezan en 0: vídeo {info.Video.Start:0.000} s, audio {audio.Start:0.000} s"));
            Assert.True(Math.Abs(info.Video.Duration - audio.Duration) <= 0.25,
                FormattableString.Invariant($"audio y vídeo desalineados: vídeo {info.Video.Duration:0.00} s, audio {audio.Duration:0.00} s"));
        }
    }

    /// <summary>Segundos de media del progreso (el motor lo da como timecode a la tasa nominal: 25 aquí).</summary>
    private static double Seconds(Timecode tc) => tc.Hours * 3600 + tc.Minutes * 60 + tc.Seconds + tc.Frames / 25.0;

    private sealed record StreamInfo(string Type, string? Codec, string? Profile, int Width, int Height, string? PixelFormat,
        string? FrameRate, int SampleRate, int Channels, double Start, double Duration);

    private sealed record MediaInfo(double Duration, IReadOnlyList<StreamInfo> Streams)
    {
        public StreamInfo Video => Streams.First(s => s.Type == "video");
        public StreamInfo Audio => Streams.First(s => s.Type == "audio");

        /// <summary>Lo que dos piezas deben compartir para unirse sin recodificar.</summary>
        public string Format => string.Join(" | ", Streams.Select(s => s.Type == "video"
            ? $"{s.Codec} {s.Profile} {s.Width}x{s.Height} {s.PixelFormat} {s.FrameRate}"
            : $"{s.Codec} {s.SampleRate} Hz {s.Channels} ch"));
    }

    private static async Task<MediaInfo> ProbeAsync(string ffprobe, string file)
    {
        var (exit, output, err) = await RunAsync(ffprobe, "-v", "error", "-show_entries",
            "format=duration:stream=codec_type,codec_name,profile,width,height,pix_fmt,r_frame_rate,sample_rate,channels,start_time,duration",
            "-of", "json", file);
        Assert.True(exit == 0, $"ffprobe falló con {Path.GetFileName(file)}: {err}");
        using var json = JsonDocument.Parse(output);
        var streams = new List<StreamInfo>();
        if (json.RootElement.TryGetProperty("streams", out var list))
            foreach (var s in list.EnumerateArray())
                streams.Add(new StreamInfo(Text(s, "codec_type") ?? "", Text(s, "codec_name"), Text(s, "profile"),
                    (int)Number(s, "width", 0), (int)Number(s, "height", 0), Text(s, "pix_fmt"), Text(s, "r_frame_rate"),
                    (int)Number(s, "sample_rate", 0), (int)Number(s, "channels", 0), Number(s, "start_time", double.NaN), Number(s, "duration", double.NaN)));
        double duration = json.RootElement.TryGetProperty("format", out var format) ? Number(format, "duration", double.NaN) : double.NaN;
        return new MediaInfo(duration, streams);

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
        static double Number(JsonElement e, string name, double missing) =>
            double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : missing;
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, (await stderr).Trim());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Esperando: " + what);
            await Task.Delay(50);
        }
    }

    private static string NewDirectory(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"baioss-slate-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Sumidero del preview, como el del motor: acepta la conexión de FFmpeg y lee (y tira) los frames BGRA. Sin
    /// lector, FFmpeg se bloquearía al escribir el preview y con él toda la tubería.</summary>
    private sealed class PreviewDrain : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public PreviewDrain()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(DrainAsync);
        }

        public int Port { get; }

        private async Task DrainAsync()
        {
            var buffer = new byte[1 << 20];
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    await using var stream = client.GetStream();
                    while (await stream.ReadAsync(buffer, _cts.Token) > 0) { }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { /* el proceso cerró la conexión: se vuelve a aceptar */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch { /* cancelación */ }
            _cts.Dispose();
        }
    }
}

/// <summary>
/// Entrada de prueba que se puede «desenchufar». Con señal entrega un generador lavfi a tiempo real (imagen de prueba + tono,
/// como una tarjeta o un emisor); sin ella no deja construir la tubería, como las fuentes reales. Cómo se entera el motor de la
/// caída y de la vuelta depende del tipo, igual que en producción:
/// <list type="bullet">
///   <item><see cref="SlateRealTimeTests.InputKind.Device"/> (DirectShow, DeckLink sin relé): un solo proceso abre el
///   dispositivo; la fuente no avisa de nada —la caída llega porque el proceso muere— y el motor SONDEA la entrada para saber
///   si la señal volvió.</item>
///   <item><see cref="SlateRealTimeTests.InputKind.Relay"/> (DeckLink con relé): dos procesos pueden leer a la vez (el motor
///   arranca el entrante antes de retirar el saliente y releva el preview con su primer frame), y la fuente avisa ella misma de
///   la caída y de la vuelta.</item>
///   <item><see cref="SlateRealTimeTests.InputKind.RelayWithPreroll"/> (SRT/RTMP): como el relé, pero el proceso entrante solo
///   toma el preview cuando sus frames llegan a cadencia real.</item>
/// </list>
/// Sin señal no tiene formato, como NDI y las entradas de red: la carta de ajuste toma el del preset. Con más de 2 canales de
/// audio (y los pares elegidos) se comporta como el audio embebido multicanal de una DeckLink.
/// </summary>
internal sealed class SwitchableSignalSource : ICaptureSource
{
    private readonly SignalInfo _present;
    private readonly SignalInfo _absent;
    private readonly SlateRealTimeTests.InputKind _kind;
    private int _probes;

    public SwitchableSignalSource(SlateRealTimeTests.InputKind kind, int audioChannels = 2, string? pairs = null)
    {
        _kind = kind;
        AudioChannelCount = audioChannels;
        if (pairs is not null) Definition.Parameters[AudioSelection.PairsKey] = pairs;
        _present = new SignalInfo(SignalState.Locked, Resolution.Hd720, FrameRate.P25, AudioLayout.Stereo, HasAudio: true,
            Timecode: null, Bitrate: null, AudioChannels: audioChannels);
        _absent = _present with { State = SignalState.NoSignal, Resolution = null, FrameRate = null };
    }

    public InputSource Definition { get; } = new() { Name = "conmutable", Type = InputType.File };
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

    public int AudioChannelCount { get; }
    public bool SupportsOverlappingProcesses => _kind != SlateRealTimeTests.InputKind.Device;
    public bool SelfReportsRecovery => _kind != SlateRealTimeTests.InputKind.Device;
    public bool DeliversPreroll => _kind == SlateRealTimeTests.InputKind.RelayWithPreroll;

    /// <summary>Veces que el motor sondeó la entrada para saber si la señal volvió.</summary>
    public int Probes => Volatile.Read(ref _probes);

    public Task OpenAsync(CancellationToken ct = default) { Set(_present, notify: true); return Task.CompletedTask; }
    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Se va la señal: una entrada con relé lo avisa; un dispositivo calla.</summary>
    public void LoseSignal() => Set(_absent, notify: SelfReportsRecovery);

    /// <summary>Vuelve la señal: una entrada con relé lo avisa; a un dispositivo lo descubre el sondeo del motor.</summary>
    public void RestoreSignal() => Set(_present, notify: SelfReportsRecovery);

    private void Set(SignalInfo signal, bool notify)
    {
        CurrentSignal = signal;
        if (notify) SignalChanged?.Invoke(this, signal);
    }

    public IReadOnlyList<string> BuildInputArguments()
    {
        if (CurrentSignal.State != SignalState.Locked) throw new InvalidOperationException("Sin señal (simulado).");
        string layout = AudioChannelCount == 2 ? "stereo" : string.Create(CultureInfo.InvariantCulture, $"{AudioChannelCount}c");
        return new[]
        {
            "-re", "-f", "lavfi", "-i",
            $"testsrc2=size=1280x720:rate=25[out0];sine=frequency=1000:sample_rate=48000,aformat=channel_layouts={layout}[out1]",
        };
    }

    public IReadOnlyList<string> BuildProbeArguments()
    {
        Interlocked.Increment(ref _probes);
        return BuildInputArguments();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
