using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Verifica el motor de captura UNIFICADO (<see cref="FfmpegChannelEngine"/>): un solo proceso FFmpeg
/// abre la fuente una vez y entrega preview Y grabación a la vez. Se prueba con la fuente de archivo,
/// que ejercita exactamente el mismo pipeline (split → preview por TCP + archivo) que un dispositivo
/// en vivo (DeckLink), sin necesitar la tarjeta.
/// </summary>
public sealed class LivePipelineTests
{
    private volatile bool _recording;
    private int _idleFrames;
    private int _recFrames;

    [SkippableFact]
    public async Task PreviewKeepsFlowing_WhileRecording_FromOneProcess()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-live-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = new FileCaptureSource(new InputSource
            {
                Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
                Parameters = { ["loop"] = "1", ["realtime"] = "1" },
                ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
            });
            await source.OpenAsync();

            var profile = new RecordingProfile
            {
                Name = "live", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };

            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            engine.FrameReady += (_, _) =>
            {
                if (_recording) Interlocked.Increment(ref _recFrames);
                else Interlocked.Increment(ref _idleFrames);
            };
            Segment? segment = null;
            engine.SegmentClosed += (_, s) => segment = s;

            // 1) Preview siempre activo: deben llegar frames en idle (sin grabar).
            await engine.StartPreviewAsync(source, profile, "TST");
            await WaitForAsync(() => Volatile.Read(ref _idleFrames) >= 3, TimeSpan.FromSeconds(20));
            Assert.True(Volatile.Read(ref _idleFrames) >= 1, "El preview debe entregar frames en idle.");

            // 2) Al grabar, el preview NO se interrumpe: deben seguir llegando frames.
            _recording = true;
            await engine.StartRecordingAsync(Guid.NewGuid(), profile);
            await WaitForAsync(() => Volatile.Read(ref _recFrames) >= 3, TimeSpan.FromSeconds(20));
            Assert.True(Volatile.Read(ref _recFrames) >= 1, "El preview debe SEGUIR fluyendo durante la grabación.");

            await Task.Delay(TimeSpan.FromSeconds(2)); // graba ~2 s de contenido real
            await engine.StopRecordingAsync();

            // 3) El archivo de grabación existe, no está vacío y es un H.264 válido.
            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), $"No se generó el archivo: {file}");
            Assert.True(new FileInfo(file!).Length > 0, "El archivo de grabación está vacío.");
            Assert.NotNull(segment); // se emitió el segmento al detener
            Assert.Equal("h264", await ProbeCodecAsync(locator.FfprobePath, file!));
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task SegmentedRecording_ProducesMultipleCompleteFiles()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-seg-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = new FileCaptureSource(new InputSource
            {
                Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
                Parameters = { ["loop"] = "1", ["realtime"] = "1" },
                ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
            });
            await source.OpenAsync();

            // Segmentos de 2 s en Transport Stream (cada .ts es completo y reproducible por separado).
            var profile = new RecordingProfile
            {
                Name = "seg", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Ts,
                Segmentation = new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromSeconds(2) },
            };

            var segments = new List<Segment>();
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            engine.SegmentClosed += (_, s) => { lock (segments) segments.Add(s); };

            await engine.StartPreviewAsync(source, profile, "TST");
            await engine.StartRecordingAsync(Guid.NewGuid(), profile);
            await Task.Delay(TimeSpan.FromSeconds(7)); // ~3 cortes de 2 s
            await engine.StopRecordingAsync();

            List<Segment> snapshot;
            lock (segments) snapshot = segments.ToList();

            // Se emitió más de un segmento, todos completos y con contenido en disco.
            Assert.True(snapshot.Count >= 2, $"Se esperaban ≥2 segmentos; se emitieron {snapshot.Count}.");
            Assert.All(snapshot, s =>
            {
                Assert.Equal(SegmentStatus.Completed, s.Status);
                Assert.True(File.Exists(s.FilePath), $"Falta el segmento {s.FilePath}.");
                Assert.True(new FileInfo(s.FilePath).Length > 0, $"Segmento vacío: {s.FilePath}.");
            });
            // Índices consecutivos desde 0 → reconstruyen la continuidad temporal.
            Assert.Equal(Enumerable.Range(0, snapshot.Count), snapshot.Select(s => s.Index).OrderBy(i => i));
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task ManualRecording_RenamedOnStop_UsesNameAndDedupes()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-name-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = new FileCaptureSource(new InputSource
            {
                Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
                Parameters = { ["loop"] = "1", ["realtime"] = "1" },
                ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
            });
            await source.OpenAsync();

            var profile = new RecordingProfile
            {
                Name = "named", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };

            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, profile, "TST");

            // 1ª grabación SIN nombre (manual): sale con nombre temporal {canal}_{fecha_hora}…
            await engine.StartRecordingAsync(Guid.NewGuid(), profile);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await engine.StopRecordingAsync();
            var temp = engine.LastOutputFile;
            Assert.NotNull(temp);
            Assert.Contains("TST_", Path.GetFileName(temp!));   // nombre temporal por canal

            // …y al DETENER se renombra al nombre elegido → «Mi Toma.mp4» (el temporal desaparece).
            var pairs = engine.RenameSessionFiles("Mi Toma");
            Assert.Single(pairs);
            var first = engine.LastOutputFile;
            Assert.EndsWith("Mi Toma.mp4", first!);
            Assert.True(File.Exists(first!), $"Falta {first}");
            Assert.False(File.Exists(temp!), "El archivo temporal debió moverse.");
            // El renombrado ESPERÓ al remux faststart: el archivo final está des-fragmentado (sin moof) y es el
            // optimizado, no el fMP4 original (la carrera dejaría un huérfano o un archivo sin índice de seek).
            Assert.False(FileContainsAscii(first!, "moof"), "La grabación renombrada debe quedar des-fragmentada (faststart).");
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(first!)!, Path.GetFileNameWithoutExtension(first!) + ".faststart.mp4")),
                "No debe quedar el temporal del remux (.faststart.mp4).");

            // 2ª grabación con el MISMO nombre → no choca: «Mi Toma 1.mp4».
            await engine.StartRecordingAsync(Guid.NewGuid(), profile);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await engine.StopRecordingAsync();
            engine.RenameSessionFiles("Mi Toma");
            var second = engine.LastOutputFile;
            Assert.EndsWith("Mi Toma 1.mp4", second!);          // dedupe « 1» al final
            Assert.True(File.Exists(second!), $"Falta {second}");
            Assert.True(File.Exists(first!), "La 1ª grabación debe seguir existiendo.");
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task Preview_ReusesFrameBuffers_NoPerFrameAllocation()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-ring-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = new FileCaptureSource(new InputSource
            {
                Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
                Parameters = { ["loop"] = "1", ["realtime"] = "1" },
                ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
            });
            await source.OpenAsync();

            var profile = new RecordingProfile
            {
                Name = "ring", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };

            // Referencias DISTINTAS de byte[] entregadas: con el anillo deben repetirse (reutilización).
            var distinct = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
            int frames = 0;
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            engine.FrameReady += (_, f) => { lock (distinct) { distinct.Add(f.Bgra); frames++; } };

            await engine.StartPreviewAsync(source, profile, "RNG");
            await WaitForAsync(() => { lock (distinct) return frames >= 12; }, TimeSpan.FromSeconds(20));

            int total, unique;
            lock (distinct) { total = frames; unique = distinct.Count; }
            Assert.True(total >= 12, $"Se esperaban ≥12 frames de preview; llegaron {total}.");
            // Muchos frames pero, como mucho, ringSize (3) buffers distintos → no se asigna uno por frame.
            Assert.True(unique <= 3, $"Se esperaban ≤3 buffers reutilizados; hubo {unique} distintos en {total} frames (¿asignación por frame?).");
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task Preview_RecoversAutomatically_WhenSignalArrivesLate()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-await-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            // Fuente que NO tiene señal en la 1ª apertura y SÍ a partir de la 2ª (simula una NDI cuyo emisor
            // tarda en aparecer): BuildInputArguments LANZA mientras no hay señal, igual que NdiCaptureSource.
            var source = new DelayedSignalSource(TestAssets.Clip!, openTilSignal: 2);
            await source.OpenAsync(); // 1ª apertura (como en ChannelHost.BuildRuntime): aún sin señal
            Assert.NotEqual(SignalState.Locked, source.CurrentSignal.State);

            var profile = new RecordingProfile
            {
                Name = "await", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };

            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            int frames = 0;
            engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);

            // Arranca el preview con la fuente SIN señal: NO debe lanzar (antes tumbaba el canal a simulado).
            await engine.StartPreviewAsync(source, profile, "AWT");
            Assert.Equal(0, Volatile.Read(ref frames)); // sin señal todavía → sin preview

            // El bucle de espera reintenta abrir la fuente; al llegar la señal, el preview se activa SOLO.
            await WaitForAsync(() => Volatile.Read(ref frames) >= 1, TimeSpan.FromSeconds(25));
            Assert.True(Volatile.Read(ref frames) >= 1, "El preview debe activarse solo al llegar la señal.");
            Assert.True(source.Opens >= 2, "Debe haber reintentado abrir la fuente hasta tener señal.");
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
            await Task.Delay(100);
    }

    private static async Task<string> ProbeCodecAsync(string ffprobePath, string file)
    {
        for (int attempt = 0; ; attempt++)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", file })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            if (output.Length > 0 || attempt >= 3) return output;
            await Task.Delay(400);
        }
    }

    /// <summary>True si el archivo contiene el fourcc ASCII dado (p. ej. el box «moof» de un MP4 fragmentado).</summary>
    private static bool FileContainsAscii(string path, string token)
    {
        var bytes = File.ReadAllBytes(path);
        var pat = System.Text.Encoding.ASCII.GetBytes(token);
        for (int i = 0; i <= bytes.Length - pat.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pat.Length; j++) if (bytes[i + j] != pat[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}

/// <summary>
/// Fuente de prueba que reporta «sin señal» hasta la N-ésima apertura y, a partir de ahí, señal del clip
/// (BuildInputArguments LANZA mientras no hay señal, como <c>NdiCaptureSource</c> sin receptor). Simula una
/// fuente NDI cuyo emisor tarda en aparecer, para verificar la auto-recuperación del preview.
/// </summary>
internal sealed class DelayedSignalSource : ICaptureSource
{
    private readonly string _clipPath;
    private readonly int _openTilSignal;
    private int _opens;

    public DelayedSignalSource(string clipPath, int openTilSignal)
    {
        _clipPath = clipPath;
        _openTilSignal = openTilSignal;
    }

    public InputSource Definition { get; } = new() { Name = "delayed", Type = InputType.File };
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;
    public int Opens => Volatile.Read(ref _opens);

    public Task OpenAsync(CancellationToken ct = default)
    {
        CurrentSignal = Interlocked.Increment(ref _opens) >= _openTilSignal
            ? new SignalInfo(SignalState.Locked, Resolution.Hd720, FrameRate.P25, AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null)
            : SignalInfo.None;
        SignalChanged?.Invoke(this, CurrentSignal);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments()
    {
        if (CurrentSignal.State != SignalState.Locked)
            throw new InvalidOperationException("Fuente sin señal (simulado).");
        return new[] { "-stream_loop", "-1", "-re", "-i", _clipPath };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
