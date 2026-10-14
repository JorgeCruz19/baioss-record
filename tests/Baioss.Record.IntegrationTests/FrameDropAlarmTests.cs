using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// La alarma de frames perdidos con FFmpeg REAL:
/// <list type="number">
///   <item>Con el canal completo (motor unificado + <see cref="StandaloneChannelEngine"/>), una fuente en tiempo real a 30
///   fps grabada con un preset a 25 NO la enciende —FFmpeg tira 1 de cada 6 frames a propósito— y el archivo sale a 25.</item>
///   <item>Con el canal completo, una saturación de verdad (1080p60 codificado en x265: el proceso no llega al tiempo real)
///   SÍ la enciende, aunque el preset convierta además la cadencia.</item>
///   <item>Con los contadores reales de FFmpeg cuando SÍ incluyen la conversión —la grabación como primera salida: lo que
///   suman los FFmpeg ≤ 6.0, que el cliente puede traer—, la regla nueva la descuenta y la antigua saltaba.</item>
/// </list>
/// </summary>
public sealed class FrameDropAlarmTests
{
    private readonly ITestOutputHelper _output;
    public FrameDropAlarmTests(ITestOutputHelper output) => _output = output;

    private static RecordingProfile Profile(VideoCodec codec, FrameRate outputRate) => new()
    {
        Name = "cadencia", VideoCodec = codec, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(4), GopSize = 50, OutputFrameRate = outputRate,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    private static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Lo que se ve del canal mientras graba: si se encendió la alarma y la telemetría cruda del proceso.</summary>
    private sealed class Watch
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(double Wall, RecorderStats Stats)> _samples = new();
        public volatile bool Alarm;
        public double? AlarmAt;

        public void OnStatus(ChannelStatus status)
        {
            if (status.RecordingState == RecordingState.Recording && !Alarm &&
                status.Alarms?.Any(a => a.Type == AlarmType.FramesDropped) == true)
            {
                AlarmAt = _clock.Elapsed.TotalSeconds;
                Alarm = true;
            }
        }

        public void OnStats(RecorderStats stats) { lock (_samples) _samples.Add((_clock.Elapsed.TotalSeconds, stats)); }

        public double Now => _clock.Elapsed.TotalSeconds;

        /// <summary>Velocidad media (tiempo de medios ÷ tiempo de pared) de los últimos <paramref name="seconds"/>.</summary>
        public double RecentSpeed(double seconds)
        {
            lock (_samples)
            {
                var valid = _samples.Where(s => s.Stats.MediaTimeUs > 0).ToList();
                if (valid.Count < 2) return double.NaN;
                var last = valid[^1];
                var first = valid.LastOrDefault(s => last.Wall - s.Wall >= seconds);
                if (first.Stats is null) first = valid[0];
                double wall = last.Wall - first.Wall;
                return wall <= 0 ? double.NaN : (last.Stats.MediaTimeUs - first.Stats.MediaTimeUs) / 1e6 / wall;
            }
        }

        public long MaxRawDrops { get { lock (_samples) return _samples.Count == 0 ? 0 : _samples.Max(s => s.Stats.DroppedFrames); } }
    }

    [SkippableFact]
    public async Task ThirtyFpsSource_RecordedWithA25fpsPreset_DoesNotRaiseTheAlarm_AndTheFileIs25fps()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDir("baioss-cadence");
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        var source = new LavfiLiveSource(640, 360, FrameRate.P30);
        try
        {
            await source.OpenAsync();
            var profile = Profile(VideoCodec.H264x264, FrameRate.P25);
            var watch = new Watch();
            await using var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger("Capture")) { OutputRoot = dir };
            capture.StatsUpdated += (_, s) => { if (capture.State == RecordingState.Recording) watch.OnStats(s); };
            await capture.StartPreviewAsync(source, profile, "CNV");
            await using var channel = new StandaloneChannelEngine("CNV", source, profile, capture,
                log: new Logger<StandaloneChannelEngine>(loggers));
            channel.StatusChanged += (_, st) => watch.OnStatus(st);

            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            await Task.Delay(TimeSpan.FromSeconds(16)); // de sobra: la alarma necesitaría 3 muestras seguidas (y la velocidad, 12)
            long shownDropped = channel.Status.Stats.DroppedFrames;
            double speed = watch.RecentSpeed(10);
            await channel.StopRecordingAsync(RecordingStopReason.Operator);

            _output.WriteLine($"velocidad últimos 10 s: {speed:0.00}x · drop_frames crudo máximo: {watch.MaxRawDrops} · perdidos mostrados: {shownDropped}");
            Assert.False(watch.Alarm, $"la conversión de 30 a 25 fps no es saturación, pero la alarma saltó a los {watch.AlarmAt:0.0} s");
            Assert.Equal(0, shownDropped);
            Assert.InRange(speed, 0.9, 1.2); // el equipo iba sobrado: si no, la prueba no demostraría nada

            // La conversión ocurrió de verdad: el archivo es de 25 fps.
            var file = capture.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), $"no se generó el archivo: {file}");
            Assert.Equal("25/1", await ProbeVideoAsync(locator.FfprobePath, file!, "r_frame_rate"));

            // Y quedó dicho en el registro (pre-vuelo), sin bloquear nada.
            lock (logs)
                Assert.Contains(logs, l => l.StartsWith("[Warning]", StringComparison.Ordinal) &&
                                           l.Contains("la fuente llega a 30 fps y el preset graba a 25 fps", StringComparison.Ordinal));
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    [SkippableFact]
    public async Task RealSaturation_StillRaisesTheAlarm_EvenWithAConversion()
    {
        // 1080p60 en tiempo real codificado con x265 (veryfast): en el portátil de desarrollo (i5-10300H) el proceso va al
        // ~55 % del tiempo real y FFmpeg NO descarta nada (medido): solo el atraso sostenido lo delata. El preset además
        // convierte de 60 a 50 fps: la conversión descontada no debe tapar la saturación.
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDir("baioss-saturation");
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        var source = new LavfiLiveSource(1920, 1080, FrameRate.P60);
        try
        {
            await source.OpenAsync();
            var profile = Profile(VideoCodec.H265x265, FrameRate.P50);
            var watch = new Watch();
            await using var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger("Capture")) { OutputRoot = dir };
            capture.StatsUpdated += (_, s) => { if (capture.State == RecordingState.Recording) watch.OnStats(s); };
            await capture.StartPreviewAsync(source, profile, "SAT");
            await using var channel = new StandaloneChannelEngine("SAT", source, profile, capture,
                log: new Logger<StandaloneChannelEngine>(loggers));
            channel.StatusChanged += (_, st) => watch.OnStatus(st);

            double started = watch.Now;
            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            while (!watch.Alarm && watch.Now - started < 45) await Task.Delay(250);
            double speed = watch.RecentSpeed(8);
            await channel.StopRecordingAsync(RecordingStopReason.Operator);

            _output.WriteLine($"alarma: {watch.Alarm} a los {watch.AlarmAt - started:0.0} s · velocidad últimos 8 s: {speed:0.00}x · drop_frames crudo máximo: {watch.MaxRawDrops}");
            // Un equipo capaz de codificar 1080p50 en x265 en tiempo real no se satura con esto: no hay nada que demostrar.
            Skip.If(!watch.Alarm && speed >= 0.8, $"este equipo no se satura con 1080p60 → x265 (va a {speed:0.00}x)");
            Assert.True(watch.Alarm, $"el proceso iba a {speed:0.00}x del tiempo real y la alarma no saltó");
            lock (logs)
                Assert.Contains(logs, l => l.Contains("frames perdidos — el proceso va al", StringComparison.Ordinal));
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    [SkippableFact]
    public async Task RealFfmpegCounters_ThatIncludeTheConversion_AreDiscounted_WhereTheOldRuleRaised()
    {
        // Con el FFmpeg empaquetado (≥ 6.1) el contador del canal es el del PREVIEW (primera salida, passthrough: siempre 0) y
        // la conversión de la grabación no llega a verse. Con un FFmpeg ≤ 6.0 —el cliente aporta el suyo— los descartes se
        // SUMAN de todas las salidas y la conversión sí llega: es la falsa alarma. Aquí se reproduce con el FFmpeg real
        // poniendo la grabación como PRIMERA salida: frame = frames grabados, drop_frames = los de la conversión. A la regla se
        // le pasan como los vería el canal con un FFmpeg antiguo (frames de ENTRADA = grabados − repetidos + descartados).
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var ffmpeg = Path.Combine(TestAssets.FfmpegDir!, "ffmpeg.exe");
        var dir = NewDir("baioss-counters");
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[]
                     {
                         "-nostdin", "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-stats_period", "1",
                         "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=12",
                         "-filter_complex", "[0:v]split=2[vrec][vprev];[vprev]scale=320:180,format=bgra[pv]",
                         "-map", "[vrec]", "-c:v", "libx264", "-preset", "veryfast", "-r", "25", "-f", "mp4", "-y", Path.Combine(dir, "rec.mp4"),
                         "-map", "[pv]", "-fps_mode", "passthrough", "-f", "null", "-",
                     })
                psi.ArgumentList.Add(a);

            var parser = new FfmpegProgressParser();
            var oldRule = new DropAlarmTracker();
            var detector = new SaturationDetector();
            detector.Reset(realTimeSource: true); // -re: a ritmo de tiempo real; la velocidad tampoco debe saltar
            var conversion = FrameRateConversion.For(FrameRate.P30, FrameRate.P25);
            bool oldAlarm = false, newAlarm = false;
            long rawDrops = 0, samples = 0;
            var clock = Stopwatch.StartNew();

            using var p = Process.Start(psi)!;
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
            {
                if (parser.Feed(line) is not { } s) continue;
                samples++;
                rawDrops = s.DroppedFrames;
                oldAlarm |= oldRule.Update(s.DroppedFrames);
                newAlarm |= detector.Update(new SaturationSample(s.DroppedFrames, s.FrameCount - s.DuplicatedFrames + s.DroppedFrames,
                    s.MediaTimeUs, clock.Elapsed), conversion);
            }
            await p.WaitForExitAsync();

            _output.WriteLine($"{samples} muestras · drop_frames final {rawDrops} · regla antigua {oldAlarm} · regla nueva {newAlarm} · sin explicar {detector.UnexplainedDrops}");
            Assert.Equal(0, p.ExitCode);
            Assert.True(rawDrops >= 40, $"FFmpeg debía descartar ~5 frames/s al convertir de 30 a 25 (descartó {rawDrops})");
            Assert.True(oldAlarm, "con estos contadores la regla antigua encendía la alarma: es la falsa alarma que se corrige");
            Assert.False(newAlarm, "la conversión de cadencia pedida no es saturación");
            Assert.Equal(0, detector.UnexplainedDrops);
        }
        finally { TryDelete(dir); }
    }

    private static async Task<string> ProbeVideoAsync(string ffprobe, string file, string entry)
    {
        for (int attempt = 0; ; attempt++)
        {
            var psi = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", $"stream={entry}", "-of", "default=nw=1:nk=1", file })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            // Tras detener, el archivo se verifica y se optimiza (remux) en segundo plano: se reintenta si justo se está reescribiendo.
            if (output.Length > 0 || attempt >= 10) return output;
            await Task.Delay(500);
        }
    }

    private static void TryDelete(string dir)
    {
        for (int i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch { Thread.Sleep(300); } // el remux o la verificación pueden tener aún el archivo abierto
        }
    }
}

/// <summary>
/// Fuente sintética EN TIEMPO REAL (lavfi con <c>-re</c>): vídeo testsrc2 a la resolución y la tasa pedidas más un tono
/// estéreo. Entrega a ritmo constante, como una tarjeta: si el proceso se atrasa, es el equipo
/// (<see cref="ICaptureSource.DeliversInRealTime"/>).
/// </summary>
internal sealed class LavfiLiveSource : ICaptureSource
{
    private readonly int _width;
    private readonly int _height;
    private readonly FrameRate _rate;

    public LavfiLiveSource(int width, int height, FrameRate rate)
    {
        _width = width;
        _height = height;
        _rate = rate;
    }

    public InputSource Definition { get; } = new() { Name = "lavfi en tiempo real", Type = InputType.File };
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;
    public bool DeliversInRealTime => true;

    public Task OpenAsync(CancellationToken ct = default)
    {
        CurrentSignal = new SignalInfo(SignalState.Locked, new Resolution(_width, _height), _rate, AudioLayout.Stereo,
            HasAudio: true, Timecode: null, Bitrate: null);
        SignalChanged?.Invoke(this, CurrentSignal);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments() => new[]
    {
        "-re", "-f", "lavfi", "-i",
        string.Create(CultureInfo.InvariantCulture,
            $"testsrc2=size={_width}x{_height}:rate={_rate.Numerator}/{_rate.Denominator}[out0];sine=frequency=1000:sample_rate=48000,aformat=channel_layouts=stereo[out1]"),
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
