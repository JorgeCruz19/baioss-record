using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
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
}
