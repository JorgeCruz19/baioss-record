using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Baioss.Record.Application.Capture;
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

        /// <summary>Su ritmo lo marca el SDK (clock_video): una llamada que no vuelve a tiempo es un parón.</summary>
        public NdiSenderPace Pace { get; } = new(paced: false);

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
                        using (Pace.Call("NDIlib_send_send_audio_v2")) NDIlib.send_send_audio_v2(_send, ref af);
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
            using (Pace.Call("NDIlib_send_send_video_v2")) NDIlib.send_send_video_v2(_send, ref vf);
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
            Pace.Stop();
            NDIlib.send_destroy(_send);
        }
    }

    /// <summary>
    /// Emisor NDI que marca él mismo el ritmo (sin clock_video, como un emisor que no da abasto o un grafismo que solo envía
    /// cuando cambia la imagen): declara <c>rateN/rateD</c> pero envía solo uno de cada <c>sendEvery</c> cuadros, progresivos; el
    /// audio, si lo hay, va completo y a tiempo real, un bloque por periodo. Cada segundo de la fuente lleva una marca de
    /// sincronía: un cuadro blanco y, a la vez, un pitido de 1 kHz de 100 ms; el resto es gris con la barra, y silencio. Con
    /// <c>graphics</c>, como la salida de grafismo de Offside Engine: BGRA con alfa (fondo semitransparente) y los cuadros sin marcar
    /// como progresivos, es decir, con el 0 de la estructura, que en NDI es «interleaved».
    /// </summary>
    private sealed class PacedNdiSender : IDisposable
    {
        private const int W = 1920, H = 1080, Bar = 24;
        public const double BeepSeconds = 0.1;
        private readonly IntPtr _send;
        private readonly Thread _thread;
        private volatile bool _stop;

        public PacedNdiSender(string name, int rateN, int rateD, int sendEvery, bool withAudio, bool graphics = false)
        {
            var create = new NDIlib.send_create_t { p_ndi_name = UTF.StringToUtf8(name), p_groups = IntPtr.Zero, clock_video = false, clock_audio = false };
            _send = NDIlib.send_create(ref create);
            Marshal.FreeHGlobal(create.p_ndi_name);
            if (_send == IntPtr.Zero) throw new InvalidOperationException("NDI: no se pudo crear el emisor.");
            SlotsPerMark = (int)Math.Round((double)rateN / rateD);
            if (SlotsPerMark % sendEvery != 0) throw new ArgumentException("La marca de sincronía debe caer en un cuadro enviado.");
            _thread = new Thread(() => Run(rateN, rateD, sendEvery, withAudio, graphics)) { IsBackground = true, Name = "emisor NDI a su ritmo" };
            _thread.Start();
        }

        /// <summary>Periodos entre dos marcas de sincronía (un segundo de la fuente, redondeado).</summary>
        public int SlotsPerMark { get; }
        /// <summary>Su ritmo (duerme hasta cada periodo) y sus parones, en su reloj.</summary>
        public NdiSenderPace Pace { get; } = new(paced: true);

        private void Run(int rateN, int rateD, int sendEvery, bool withAudio, bool graphics)
        {
            int bytesPerPixel = graphics ? 4 : 2, stride = W * bytesPerPixel;
            var frame = new byte[stride * H];
            var gray = new byte[stride];
            var white = new byte[stride * H];
            if (graphics)
            {
                for (int x = 0; x < W; x++) { gray[x * 4] = gray[x * 4 + 1] = gray[x * 4 + 2] = 37; gray[x * 4 + 3] = 160; } // B G R A
                Array.Fill(white, (byte)255);
            }
            else
            {
                for (int x = 0; x < W; x += 2) { gray[x * 2] = 128; gray[x * 2 + 1] = 48; gray[x * 2 + 2] = 128; gray[x * 2 + 3] = 48; } // U Y V Y
                for (int i = 0; i < white.Length; i += 2) { white[i] = 128; white[i + 1] = 235; }
            }
            IntPtr video = Marshal.AllocHGlobal(frame.Length);
            IntPtr audio = Marshal.AllocHGlobal(2 * 4 * 4096);
            var samples = new float[2 * 4096];
            double fps = (double)rateN / rateD;
            long n = 0, sent = 0;
            try
            {
                while (!_stop)
                {
                    double due = n / fps, wait = due - Pace.Now;
                    if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                    Pace.Period(due, (n + 1) / fps);
                    if (n % sendEvery == 0)
                    {
                        if (n % SlotsPerMark == 0) Buffer.BlockCopy(white, 0, frame, 0, frame.Length);
                        else
                        {
                            int x0 = ((int)(W / 2.0 - Bar / 2.0 + W * 0.45 * Math.Sin(Math.PI * due))) & ~1;
                            for (int y = 0; y < H; y++)
                            {
                                Buffer.BlockCopy(gray, 0, frame, y * stride, stride);
                                if (graphics) frame.AsSpan(y * stride + x0 * 4, Bar * 4).Fill(255); // blanca y opaca
                                else for (int x = x0; x < x0 + Bar; x++) frame[y * stride + x * 2 + 1] = 235;
                            }
                        }
                        Marshal.Copy(frame, 0, video, frame.Length);
                        var vf = new NDIlib.video_frame_v2_t
                        {
                            xres = W, yres = H, FourCC = graphics ? NDIlib.FourCC_type_e.FourCC_type_BGRA : NDIlib.FourCC_type_e.FourCC_type_UYVY,
                            frame_rate_N = rateN, frame_rate_D = rateD, picture_aspect_ratio = 16f / 9f,
                            // El grafismo deja el campo sin rellenar: el 0, que NDI lee como «interleaved».
                            frame_format_type = graphics ? default : NDIlib.frame_format_type_e.frame_format_type_progressive,
                            timecode = NDIlib.send_timecode_synthesize, p_data = video, line_stride_in_bytes = stride, p_metadata = IntPtr.Zero,
                        };
                        using (Pace.Call("NDIlib_send_send_video_v2")) NDIlib.send_send_video_v2(_send, ref vf);
                    }
                    if (withAudio)
                    {
                        // El audio de este periodo, completo: silencio salvo los 100 ms del pitido tras cada marca.
                        long target = (long)Math.Round((n + 1) * 48000.0 / fps);
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
                        using (Pace.Call("NDIlib_send_send_audio_v2")) NDIlib.send_send_audio_v2(_send, ref af);
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

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
            Pace.Stop();
            NDIlib.send_destroy(_send);
        }
    }

    /// <summary>
    /// Una fuente que entrega MENOS cuadros de los que declara (la mitad; o un grafismo que declara 60 y manda 4) se grababa
    /// comprimida en el tiempo: FFmpeg lee el vídeo a la tasa declarada y le pone las marcas por contador, así que 8 s de fuente
    /// a media cadencia eran 4 s de vídeo, con el audio cada vez más desfasado. El receptor le da a cada cuadro la marca de su
    /// periodo (en NUT) y la grabación repite el último hasta el siguiente: en el archivo, las marcas de sincronía (cuadro blanco +
    /// pitido, una por segundo de la fuente) están a un segundo unas de otras, a la cadencia del preset, y el audio va con el vídeo
    /// de la primera a la última. El preview pinta los cuadros reales, a su ritmo, y FPS OUT dice la cadencia que se graba. Un
    /// grafismo como el de Offside Engine (BGRA con alfa, «interleaved» a 60000/1000) es un 60p: ni «120i» ni desentrelazado, y
    /// el receptor lo sirve en UYVY.
    /// </summary>
    [SkippableTheory]
    [InlineData(30000, 1001, 1, true, false)]    // a su ritmo: control de la medida
    [InlineData(30000, 1001, 2, true, false)]    // la mitad de lo que declara, con audio completo
    [InlineData(30000, 1001, 2, false, false)]   // la mitad, sin audio: el silencio sigue a los periodos (los que rellena FFmpeg incluidos)
    [InlineData(60, 1, 15, false, false)]        // un grafismo: declara 60, manda 4 y no trae audio
    [InlineData(60000, 1000, 15, false, true)]   // …y como Offside Engine: BGRA con alfa, cuadros marcados «interleaved»
    public async Task ASourceSendingFewerFramesThanDeclared_IsRecordedInRealTime_InSyncWithItsAudio(int rateN, int rateD, int sendEvery, bool withAudio, bool graphics)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        Skip.IfNot(NdiRuntime.IsAvailable, "Runtime NDI no disponible (Processing.NDI.Lib.x64.dll junto a las pruebas).");
        string unique = $"Baioss ritmo {Guid.NewGuid().ToString("N")[..8]}";
        using var sender = new PacedNdiSender(unique, rateN, rateD, sendEvery, withAudio, graphics);
        string? name = null;
        for (int i = 0; i < 10 && name is null; i++) name = NdiRuntime.DiscoverSources(1000).FirstOrDefault(s => s.Contains(unique, StringComparison.Ordinal));
        Assert.True(name is not null, $"El emisor de prueba «{unique}» no aparece entre las fuentes NDI.");
        double declared = (double)rateN / rateD, markPeriod = sender.SlotsPerMark / declared;

        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-ndi-ritmo-{Guid.NewGuid():N}");
        var log = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (log) log.Add(l); });
        var source = new NdiCaptureSource(new InputSource { Name = name!, Type = InputType.Ndi, Uri = name }, loggers.CreateLogger("Ndi"));
        try
        {
            await source.OpenAsync();
            Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
            Assert.Equal(declared, source.CurrentSignal.FrameRate!.Value.Value, 3);
            // Progresivo en los dos casos (el grafismo marca «interleaved» a 60, que no es un entrelazado) y servido en UYVY.
            Assert.Equal(ScanType.Progressive, source.CurrentSignal.Scan);
            Assert.Equal($"1920×1080 · {VideoModes.FormatRate(declared)}p · NDI", source.CurrentSignal.FormatLabel);
            Assert.Equal("uyvy422", source.VideoPixelFormat);

            var profile = new RecordingProfile
            {
                Name = "ndi-ritmo", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(4), GopSize = 30,
                TargetResolution = new Resolution(1280, 720), OutputFrameRate = FrameRate.P2997,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };
            var clock = Stopwatch.StartNew();
            var previewAt = new List<double>();
            await using var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine")) { OutputRoot = outputRoot };
            engine.FrameReady += (_, _) => { lock (previewAt) previewAt.Add(clock.Elapsed.TotalSeconds); };
            await engine.StartPreviewAsync(source, profile, "NDI");
            await RecordingRecoveryWakeTests.WaitUntilAsync(() => { lock (previewAt) return previewAt.Count > 15; }, TimeSpan.FromSeconds(20), "preview de la fuente NDI");
            await Task.Delay(1000);
            var (p0, p1) = await OnTimeWindowAsync(sender.Pace, clock);
            double previewFps;
            lock (previewAt) previewFps = previewAt.Count(t => t >= p0 && t < p1) / (p1 - p0);
            double fpsOut = engine.Stats.OutputFps;

            await sender.Pace.WaitOnTimeAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            double recordFrom = sender.Pace.Now;
            var recording = Stopwatch.StartNew();
            await engine.StartRecordingAsync(Guid.NewGuid(), profile, baseName: "ndi_ritmo");
            await Task.Delay(8000);
            await engine.StopRecordingAsync();
            double recordedFor = recording.Elapsed.TotalSeconds;
            sender.Pace.SkipIfStalledWhileRecording(recordFrom - 1, sender.Pace.Now);
            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, $"Sin archivo: {file}");
            var (vDur, vPackets, aDur, hasAudioTrack, vStart, aStart) = await ProbeStartsAsync(locator.FfprobePath, file!);
            var (flashes, beeps) = await SyncMarksAsync(locator.FfmpegPath, file!);
            double maxVolume = hasAudioTrack ? await MaxVolumeAsync(locator.FfmpegPath, file!) : double.NaN;
            string cadenceLog, recordPipeline;
            lock (log)
            {
                cadenceLog = log.LastOrDefault(l => l.Contains("que declara", StringComparison.Ordinal)) ?? "(sin aviso)";
                recordPipeline = log.LastOrDefault(l => l.Contains("Pipeline canal NDI (grabación+preview)", StringComparison.Ordinal)) ?? "";
            }

            double flashSpan = flashes.Count > 1 ? flashes[^1] - flashes[0] : 0;
            // Cada destello con su pitido (el más cercano, si lo hay a menos de medio segundo).
            var offsets = flashes.Select(f => beeps.Select(b => b - f).Where(o => Math.Abs(o) < 0.5).DefaultIfEmpty(double.NaN).First())
                                 .Where(o => !double.IsNaN(o)).ToList();
            string I(double d) => d.ToString("0.000", CultureInfo.InvariantCulture);
            _out.WriteLine($"{declared:0.##} declarados{(graphics ? " (BGRA con alfa, «interleaved»)" : "")}, 1 de cada {sendEvery} enviado, " +
                           $"{(withAudio ? "con" : "sin")} audio (emisor {sender.Pace.Summary()}): «{source.CurrentSignal.FormatLabel}» · " +
                           $"preview {previewFps:0.00} fps, FPS OUT {fpsOut:0.00} · grabado {recordedFor:0.00} s → vídeo {I(vDur)} s desde {I(vStart)} ({vPackets} cuadros), " +
                           $"audio {I(aDur)} s desde {I(aStart)}, pico {maxVolume:0.0} dBFS");
            _out.WriteLine($"  marcas de vídeo: {string.Join(" ", flashes.Select(I))}");
            _out.WriteLine($"  pitidos:         {string.Join(" ", beeps.Select(I))}");
            if (withAudio) _out.WriteLine($"  pitido − destello: {string.Join(" ", offsets.Select(o => o.ToString("+0.000;-0.000", CultureInfo.InvariantCulture)))}");
            _out.WriteLine($"  {cadenceLog}");

            // Dura lo que dura: entre la primera y la última marca hay tantos segundos como en la fuente (antes, la mitad), y el
            // vídeo cubre lo grabado, a la cadencia declarada.
            Assert.True(flashes.Count >= 5, $"Solo {flashes.Count} marcas de vídeo en el archivo.");
            Assert.Equal((flashes.Count - 1) * markPeriod, flashSpan, 0.07);
            Assert.InRange(vPackets / vDur, 28.5, 31.5);
            Assert.True(vDur > recordedFor - 1.5, $"{I(vDur)} s de vídeo en {recordedFor:0.00} s de grabación.");
            // El audio (o el silencio, que sigue a los periodos) cubre el vídeo salvo el final: al Detener, el proceso del
            // canal deja sin leer lo último del audio, que lleva detrás del vídeo (0,07–0,9 s medidos el 2026-10-17, también sin
            // este arreglo y con una fuente a su ritmo). La sincronía de verdad se mide con las marcas.
            Assert.True(aDur > vDur - 1.0, $"Vídeo {I(vDur)} s frente a audio {I(aDur)} s.");
            // El preview pinta los cuadros que manda la fuente —a su ritmo: ni las repeticiones de antes ni más (como mucho uno de
            // mantenimiento si un hueco pasa de 300 ms)—, y FPS OUT dice la cadencia que se graba, la declarada.
            double real = declared / sendEvery;
            Assert.InRange(previewFps, 0.85 * real, 1.15 * real + 1.0 / (p1 - p0));
            Assert.InRange(fpsOut, 0.85 * declared, 1.15 * declared);
            if (withAudio)
            {
                // Y el audio con el vídeo, de la primera marca a la última: el mismo desfase (sin deriva) y pequeño. (La última marca
                // puede caer ya en el final sin audio.)
                Assert.True(offsets.Count >= flashes.Count - 1, $"Solo {offsets.Count} pitidos para {flashes.Count} marcas.");
                Assert.All(offsets, o => Assert.InRange(o, -0.1, 0.1));
                Assert.InRange(offsets.Max() - offsets.Min(), 0, 0.06);
            }
            else Assert.True(double.IsNegativeInfinity(maxVolume) || maxVolume < -80, $"Sin audio en la fuente, la pista debe ser silencio ({maxVolume:0.0} dBFS).");
            if (sendEvery > 1) Assert.Contains("que declara", cadenceLog);
            // El vídeo llega en NUT, con sus marcas, y la grabación lo rellena al final de su rama, a la cadencia del preset. Nada que
            // desentrelazar: ni bwdif en la grabación ni un solo campo en el preview.
            Assert.Contains("-f nut", recordPipeline);
            Assert.Contains("format=yuv420p,fps=30000/1001[vmain]", recordPipeline);
            Assert.DoesNotContain("bwdif", recordPipeline);
            Assert.DoesNotContain("field=type", recordPipeline);
        }
        finally
        {
            await source.DisposeAsync();
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Instantes (s) de las marcas de sincronía de un archivo: el primer cuadro de cada destello (brillo medio alto tras uno oscuro)
    /// y el arranque de cada pitido (pico de un bloque de 5 ms por encima de −30 dBFS tras uno en silencio). Una sola pasada de
    /// FFmpeg, para que vídeo y audio compartan el origen de tiempos.
    /// </summary>
    private static async Task<(List<double> Flashes, List<double> Beeps)> SyncMarksAsync(string ffmpeg, string file)
    {
        var err = await RunAsync(ffmpeg, true, "-hide_banner", "-nostats", "-i", file, "-filter_complex",
            "[0:v:0]scale=64:36,format=gray,showinfo[v];[0:a:0]asetnsamples=n=240:p=0,astats=metadata=1:reset=1," +
            "ametadata=mode=print:key=lavfi.astats.Overall.Peak_level[a]",
            "-map", "[v]", "-f", "null", "-", "-map", "[a]", "-f", "null", "-");
        var flashes = new List<double>();
        var beeps = new List<double>();
        // Una marca que ya estaba en marcha al empezar el archivo no se puede fechar: solo cuentan las que empiezan tras un cuadro
        // oscuro o un bloque en silencio. Un grafismo deja su cuadro blanco 250 ms de cada segundo, y un Grabar que caía dentro
        // contaba el primer cuadro del archivo como destello (primera separación de 0,83 s en vez de 1).
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
            var (t0, t1) = await OnTimeWindowAsync(sender.Pace, clock);
            List<(double At, int Max, int Bars)> window;
            lock (frames) window = frames.Where(f => f.At >= t0 && f.At < t1).ToList();
            double previewFps = window.Count / (t1 - t0);
            int clean = window.Count(f => InterlacedPreviewTests.Clean((f.Max, f.Bars)));

            await sender.Pace.WaitOnTimeAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            double recordFrom = sender.Pace.Now;
            await engine.StartRecordingAsync(Guid.NewGuid(), profile, baseName: "ndi_syn");
            await Task.Delay(5000);
            var stop = Stopwatch.StartNew();
            await engine.StopRecordingAsync();
            stop.Stop();
            sender.Pace.SkipIfStalledWhileRecording(recordFrom - 1, sender.Pace.Now);
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
            _out.WriteLine($"{layout} {(withAudio ? "con" : "sin")} audio (emisor {sender.Pace.Summary()}): señal «{signal.FormatLabel}» · preview {previewFps:0.00} fps, " +
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

    /// <summary>
    /// Una ventana de 3 s (en <paramref name="clock"/>) en que el emisor fue a su hora, para contar lo que pinta el preview: antes
    /// espera a que lleve un segundo a su hora y, si se para dentro de la ventana, la cuenta no vale y se mide otra, hasta tres veces;
    /// sin ninguna, la prueba se omite diciendo dónde se paró. Un emisor parado ~2,5 s dejaba el preview en ~8,7 cuadros/s y la
    /// prueba fallaba (2026-10-21).
    /// </summary>
    private async Task<(double From, double To)> OnTimeWindowAsync(NdiSenderPace pace, Stopwatch clock)
    {
        var stalls = new List<NdiSenderPace.Stall>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await pace.WaitOnTimeAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
            double from = clock.Elapsed.TotalSeconds, paceFrom = pace.Now;
            await Task.Delay(3000);
            double to = clock.Elapsed.TotalSeconds;
            var spoiled = pace.Between(paceFrom, pace.Now);
            if (spoiled.Count == 0) return (from, to);
            stalls.AddRange(spoiled);
            _out.WriteLine($"  El emisor se paró en la ventana del preview ({string.Join("; ", spoiled)}): se mide otra.");
        }
        Skip.If(true, $"El emisor de la prueba se paró en las tres ventanas del preview: {string.Join("; ", stalls)}.");
        return default;
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
        var p = await ProbeStartsAsync(ffprobe, file);
        return (p.VDur, p.VPackets, p.ADur, p.HasAudio);
    }

    /// <summary>Duración, paquetes de vídeo y comienzo (start_time) de cada pista.</summary>
    private static async Task<(double VDur, long VPackets, double ADur, bool HasAudio, double VStart, double AStart)> ProbeStartsAsync(string ffprobe, string file)
    {
        var json = await RunAsync(ffprobe, false, "-v", "error", "-count_packets", "-show_entries", "stream=codec_type,duration,start_time,nb_read_packets", "-of", "json", file);
        double vDur = 0, aDur = 0, vStart = 0, aStart = 0; long vPackets = 0; bool audio = false;
        using var doc = JsonDocument.Parse(json);
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            double Num(string p) => s.TryGetProperty(p, out var v) && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
            if (s.GetProperty("codec_type").GetString() == "video") { vDur = Num("duration"); vPackets = (long)Num("nb_read_packets"); vStart = Num("start_time"); }
            else { aDur = Num("duration"); audio = true; aStart = Num("start_time"); }
        }
        return (vDur, vPackets, aDur, audio, vStart, aStart);
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
