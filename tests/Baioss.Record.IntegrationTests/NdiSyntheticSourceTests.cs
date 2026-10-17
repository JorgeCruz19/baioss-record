using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
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
/// Una fuente NDI emitida desde la propia prueba (SDK NDI) por el camino de Baioss —receptor, sockets, proceso del canal—
/// hasta el preview y la grabación: una barra blanca que cruza deprisa, 1080 a 29.97, progresiva, con los dos campos
/// entretejidos (un 1080i, como el de vMix) o en campos sueltos, con o sin audio. Sin audio el canal no tenía ni preview ni grabación (FFmpeg
/// esperaba la entrada de audio), la entrelazada se veía con doble imagen y se grababa sin desentrelazar, y al Detener se perdía el
/// final del audio. Se omite sin el runtime NDI (Processing.NDI.Lib.x64.dll junto a las pruebas).
/// </summary>
public sealed class NdiSyntheticSourceTests
{
    private readonly ITestOutputHelper _out;
    public NdiSyntheticSourceTests(ITestOutputHelper output) => _out = output;

    /// <summary>Cómo manda la fuente sus cuadros.</summary>
    public enum Layout { Progressive, Interleaved, Fields }

    /// <summary>Emisor NDI en un hilo, al ritmo de la fuente (clock_video).</summary>
    private sealed class SyntheticNdiSender : IDisposable
    {
        private const int W = 1920, H = 1080, FpsN = 30000, FpsD = 1001, Bar = 24;
        private readonly IntPtr _send;
        private readonly Thread _thread;
        private volatile bool _stop;

        public SyntheticNdiSender(string name, Layout layout, bool withAudio)
        {
            var create = new NDIlib.send_create_t { p_ndi_name = UTF.StringToUtf8(name), p_groups = IntPtr.Zero, clock_video = true, clock_audio = false };
            _send = NDIlib.send_create(ref create);
            Marshal.FreeHGlobal(create.p_ndi_name);
            if (_send == IntPtr.Zero) throw new InvalidOperationException("NDI: no se pudo crear el emisor.");
            _thread = new Thread(() => Run(layout, withAudio)) { IsBackground = true, Name = "emisor NDI de prueba" };
            _thread.Start();
        }

        private void Run(Layout layout, bool withAudio)
        {
            bool interlaced = layout is not Layout.Progressive;
            const int stride = W * 2;
            var frame = new byte[stride * H];
            var gray = new byte[stride];
            for (int x = 0; x < W; x += 2) { gray[x * 2] = 128; gray[x * 2 + 1] = 48; gray[x * 2 + 2] = 128; gray[x * 2 + 3] = 48; } // U Y V Y
            IntPtr video = Marshal.AllocHGlobal(frame.Length);
            IntPtr audio = Marshal.AllocHGlobal(2 * 4 * 2048);
            var samples = new float[2 * 2048];
            double fps = (double)FpsN / FpsD, phase = 0;
            long n = 0, sent = 0;
            int BarX(double t) => ((int)(W / 2.0 - Bar / 2.0 + W * 0.45 * Math.Sin(Math.PI * t))) & ~1;
            void Draw(int first, int step, int x0)
            {
                for (int y = first; y < H; y += step)
                {
                    Buffer.BlockCopy(gray, 0, frame, y * stride, stride);
                    for (int x = x0; x < x0 + Bar; x++) frame[y * stride + x * 2 + 1] = 235;
                }
            }
            try
            {
                while (!_stop)
                {
                    double t = n / fps;
                    if (interlaced) { Draw(0, 2, BarX(t)); Draw(1, 2, BarX(t + 0.5 / fps)); } // campo superior primero
                    else Draw(0, 1, BarX(t));
                    Marshal.Copy(frame, 0, video, frame.Length);
                    if (layout is Layout.Fields)
                    {
                        // Cada campo, suelto: el superior (líneas pares) y luego el inferior, con el doble de paso entre líneas.
                        Send(NDIlib.frame_format_type_e.frame_format_type_field_0, video, H / 2, 2 * stride);
                        Send(NDIlib.frame_format_type_e.frame_format_type_field_1, video + stride, H / 2, 2 * stride);
                    }
                    else Send(interlaced ? NDIlib.frame_format_type_e.frame_format_type_interleaved : NDIlib.frame_format_type_e.frame_format_type_progressive,
                              video, H, stride);
                    if (withAudio)
                    {
                        long target = (long)Math.Round((n + 1) * 48000.0 / fps);
                        int count = (int)(target - sent);
                        for (int i = 0; i < count; i++)
                        {
                            float s = (float)(0.1 * Math.Sin(phase)); // 1 kHz a −20 dBFS
                            phase += 2 * Math.PI * 1000 / 48000;
                            samples[i] = s; samples[count + i] = s;  // planar
                        }
                        Marshal.Copy(samples, 0, audio, 2 * count);
                        var af = new NDIlib.audio_frame_v2_t
                        {
                            sample_rate = 48000, no_channels = 2, no_samples = count, timecode = NDIlib.send_timecode_synthesize,
                            p_data = audio, channel_stride_in_bytes = count * 4, p_metadata = IntPtr.Zero,
                        };
                        NDIlib.send_send_audio_v2(_send, ref af);
                        sent = target;
                    }
                    n++;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(video);
                Marshal.FreeHGlobal(audio);
            }
        }

        private void Send(NDIlib.frame_format_type_e type, IntPtr data, int lines, int lineStride)
        {
            var vf = new NDIlib.video_frame_v2_t
            {
                xres = W, yres = lines, FourCC = NDIlib.FourCC_type_e.FourCC_type_UYVY, frame_rate_N = FpsN, frame_rate_D = FpsD,
                picture_aspect_ratio = 16f / 9f, frame_format_type = type,
                timecode = NDIlib.send_timecode_synthesize, p_data = data, line_stride_in_bytes = lineStride, p_metadata = IntPtr.Zero,
            };
            NDIlib.send_send_video_v2(_send, ref vf);
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
            NDIlib.send_destroy(_send);
        }
    }

    [SkippableTheory]
    [InlineData(Layout.Progressive, true)]  // progresiva con audio: lo de siempre
    [InlineData(Layout.Progressive, false)] // progresiva SIN audio: antes, ni preview ni grabación
    [InlineData(Layout.Interleaved, true)]  // 1080i entretejido (vMix): sin doble imagen, y la grabación progresiva desentrelaza
    [InlineData(Layout.Fields, false)]      // 1080i en campos sueltos y sin audio: se tejen en cuadros entrelazados
    public async Task ASyntheticNdiSource_IsPreviewedAndRecorded(Layout layout, bool withAudio)
    {
        bool interlaced = layout is not Layout.Progressive;
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        Skip.IfNot(NdiRuntime.IsAvailable, "Runtime NDI no disponible (Processing.NDI.Lib.x64.dll junto a las pruebas).");
        string unique = $"Baioss prueba {Guid.NewGuid().ToString("N")[..8]}";
        using var sender = new SyntheticNdiSender(unique, layout, withAudio);
        string? name = null;
        for (int i = 0; i < 10 && name is null; i++) name = NdiRuntime.DiscoverSources(1000).FirstOrDefault(s => s.Contains(unique, StringComparison.Ordinal));
        Assert.True(name is not null, $"El emisor de prueba «{unique}» no aparece entre las fuentes NDI.");

        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-ndi-syn-{Guid.NewGuid():N}");
        var log = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (log) log.Add(l); });
        var source = new NdiCaptureSource(new InputSource { Name = name!, Type = InputType.Ndi, Uri = name }, loggers.CreateLogger("Ndi"));
        try
        {
            await source.OpenAsync();
            var signal = source.CurrentSignal;
            Assert.Equal(SignalState.Locked, signal.State);
            Assert.Equal(interlaced ? ScanType.InterlacedTff : ScanType.Progressive, signal.Scan);
            Assert.Equal(interlaced ? "1920×1080 · 59.94i · NDI" : "1920×1080 · 29.97p · NDI", signal.FormatLabel);

            // Grabación progresiva a 720p29.97: con la fuente entrelazada, la rama de grabación desentrelaza.
            var profile = new RecordingProfile
            {
                Name = "ndi-syn", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(4), GopSize = 30,
                TargetResolution = new Resolution(1280, 720), OutputFrameRate = FrameRate.P2997,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };
            var clock = Stopwatch.StartNew();
            var frames = new List<(double At, int Max, int Bars)>();
            await using var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine")) { OutputRoot = outputRoot };
            engine.FrameReady += (_, f) =>
            {
                var p = InterlacedPreviewTests.RowProfile(f.Bgra, f.Width, f.Stride, f.Height / 2);
                lock (frames) frames.Add((clock.Elapsed.TotalSeconds, p.Max, p.Bars));
            };
            await engine.StartPreviewAsync(source, profile, "NDI");
            await RecordingRecoveryWakeTests.WaitUntilAsync(() => { lock (frames) return frames.Count > 30; }, TimeSpan.FromSeconds(20),
                "preview de la fuente NDI (sin audio, FFmpeg no arrancaba)");

            await Task.Delay(1000);
            double t0 = clock.Elapsed.TotalSeconds;
            await Task.Delay(3000);
            double t1 = clock.Elapsed.TotalSeconds;
            List<(double At, int Max, int Bars)> window;
            lock (frames) window = frames.Where(f => f.At >= t0 && f.At < t1).ToList();
            double previewFps = window.Count / (t1 - t0);
            int clean = window.Count(f => InterlacedPreviewTests.Clean((f.Max, f.Bars)));

            await engine.StartRecordingAsync(Guid.NewGuid(), profile, baseName: "ndi_syn");
            await Task.Delay(5000);
            var stop = Stopwatch.StartNew();
            await engine.StopRecordingAsync();
            stop.Stop();
            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, $"Sin archivo: {file}");
            var (vDur, vPackets, aDur, hasAudioTrack) = await ProbeAsync(locator.FfprobePath, file!);
            double maxVolume = hasAudioTrack ? await MaxVolumeAsync(locator.FfmpegPath, file!) : double.NaN;
            string previewPipeline, recordPipeline;
            lock (log)
            {
                previewPipeline = log.LastOrDefault(l => l.Contains("Pipeline canal NDI (preview)", StringComparison.Ordinal)) ?? "";
                recordPipeline = log.LastOrDefault(l => l.Contains("Pipeline canal NDI (grabación+preview)", StringComparison.Ordinal)) ?? "";
            }
            _out.WriteLine($"{layout} {(withAudio ? "con" : "sin")} audio: señal «{signal.FormatLabel}» · preview {previewFps:0.00} fps, " +
                           $"limpios {clean} de {window.Count} · FPS OUT {engine.Stats.OutputFps:0.00} · archivo {vDur:0.00} s, {vPackets} cuadros, audio {aDur:0.00} s, " +
                           $"pico {maxVolume:0.0} dBFS · Detener {stop.Elapsed.TotalSeconds:0.00} s");

            Assert.InRange(previewFps, 25, 35);
            Assert.Equal(window.Count, clean); // una sola barra, entera: sin doble imagen
            Assert.InRange(vPackets / vDur, 28.5, 31.5);
            Assert.True(hasAudioTrack, "La grabación debe llevar su pista de audio (silenciosa si la fuente no trae).");
            Assert.True(Math.Abs(vDur - aDur) < 0.5, $"Vídeo {vDur:0.000} s frente a audio {aDur:0.000} s."); // el umbral de NdiCaptureTests
            // Al Detener, el receptor cierra las dos entradas a la vez y FFmpeg termina solo con todo lo que llegó: el audio llega hasta
            // el final. Con la «q» acababa 0,1–0,9 s antes que el vídeo (lo que aún no había leído se perdía) y esta prueba fallaba a ratos.
            Assert.True(aDur > vDur - 0.1, $"El audio acaba {vDur - aDur:0.000} s antes que el vídeo (vídeo {vDur:0.000} s, audio {aDur:0.000} s).");
            if (withAudio) Assert.True(maxVolume > -30, $"El tono grabado está a {maxVolume:0.0} dBFS.");
            else Assert.True(double.IsNegativeInfinity(maxVolume) || maxVolume < -80, $"Sin audio en la fuente, la pista debe ser silencio ({maxVolume:0.0} dBFS).");
            if (interlaced)
            {
                Assert.Contains("[0:v]field=type=top,scale=640:360,", previewPipeline);
                Assert.Contains("[vrec]bwdif=mode=send_frame:parity=tff:deint=all,scale=1280:720,", recordPipeline);
            }
            else Assert.DoesNotContain("bwdif", recordPipeline);
        }
        finally
        {
            await source.DisposeAsync();
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }

    private static async Task<string> RunAsync(string exe, bool stderr, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stderr ? await e : await o;
    }

    private static async Task<(double VDur, long VPackets, double ADur, bool HasAudio)> ProbeAsync(string ffprobe, string file)
    {
        var json = await RunAsync(ffprobe, false, "-v", "error", "-count_packets", "-show_entries", "stream=codec_type,duration,nb_read_packets", "-of", "json", file);
        double vDur = 0, aDur = 0; long vPackets = 0; bool audio = false;
        using var doc = JsonDocument.Parse(json);
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            double Num(string p) => s.TryGetProperty(p, out var v) && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
            if (s.GetProperty("codec_type").GetString() == "video") { vDur = Num("duration"); vPackets = (long)Num("nb_read_packets"); }
            else { aDur = Num("duration"); audio = true; }
        }
        return (vDur, vPackets, aDur, audio);
    }

    private static async Task<double> MaxVolumeAsync(string ffmpeg, string file)
    {
        var err = await RunAsync(ffmpeg, true, "-hide_banner", "-nostats", "-i", file, "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-");
        var line = err.Split('\n').LastOrDefault(l => l.Contains("max_volume:", StringComparison.Ordinal));
        if (line is null) return double.NegativeInfinity;
        var v = line[(line.IndexOf("max_volume:", StringComparison.Ordinal) + 11)..].Replace("dB", "").Trim();
        return v.StartsWith("-inf", StringComparison.Ordinal) ? double.NegativeInfinity : double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
