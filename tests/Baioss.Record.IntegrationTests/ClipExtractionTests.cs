using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Channels;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Clip de los últimos N segundos de una grabación EN CURSO, con el motor real y FFmpeg: el archivo sale en <c>clips/</c>,
/// dura lo pedido (algo más por delante: arranca en el fotograma clave anterior), empieza en un fotograma clave, decodifica
/// entero y la grabación sigue como si nada. Con archivo único fMP4, con TS, y con grabación segmentada (el clip cruza
/// segmentos). Y lo que NO se puede: sin grabación, o con MP4 estándar (sin índice hasta cerrarse).
/// </summary>
public sealed class ClipExtractionTests
{
    private static RecordingProfile Profile(ContainerFormat container, SegmentationPolicy? segmentation = null) => new()
    {
        Name = "clip", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(4), GopSize = 25,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = container,
        Segmentation = segmentation,
    };

    private static FileCaptureSource ClipSource() => new(new InputSource
    {
        Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
        Parameters = { ["loop"] = "1", ["realtime"] = "1" },
        ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
    });

    /// <summary>Lo que ffprobe dice del clip: duración, si el primer paquete de vídeo es un fotograma clave, y paquetes vs frames decodificados.</summary>
    private static async Task<(double Duration, bool StartsAtKeyframe, int Packets, int Frames)> InspectAsync(string ffprobe, string file)
    {
        async Task<string> Run(params string[] args)
        {
            var psi = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string s = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
            return s.Trim();
        }
        // Primera línea de cada salida: en TS ffprobe lista la pista de vídeo también bajo su programa (dos líneas iguales).
        static string First(string s) => s.Split('\n')[0].Trim().Trim(',');
        double duration = double.Parse(First(await Run("-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file)), CultureInfo.InvariantCulture);
        string first = First(await Run("-v", "error", "-select_streams", "v:0", "-show_entries", "packet=flags", "-of", "csv=p=0", "-read_intervals", "%+#1", file));
        int packets = int.Parse(First(await Run("-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", file)), CultureInfo.InvariantCulture);
        int frames = int.Parse(First(await Run("-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", file)), CultureInfo.InvariantCulture);
        return (duration, first.Contains('K'), packets, frames);
    }

    private static async Task WaitForRecordedSecondsAsync(FfmpegChannelEngine engine, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds + 1.5) await Task.Delay(250);
        Assert.Equal(RecordingState.Recording, engine.State);
    }

    private async Task RunAsync(ContainerFormat container, SegmentationPolicy? segmentation, bool fragmented, double recordSeconds, int clipSeconds,
        Func<FfmpegChannelEngine, string, Task> assertions)
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-clip-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = ClipSource();
            await source.OpenAsync();
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot, FragmentedMp4 = fragmented };
            await engine.StartPreviewAsync(source, Profile(container, segmentation), "CLP");
            await engine.StartRecordingAsync(Guid.NewGuid(), Profile(container, segmentation));
            await WaitForRecordedSecondsAsync(engine, recordSeconds);
            await assertions(engine, outputRoot);
            await engine.StopRecordingAsync();
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public Task TheLastSeconds_OfAGrowingFragmentedMp4_BecomeAClip_WithoutStoppingTheRecording() =>
        RunAsync(ContainerFormat.Mp4, null, fragmented: true, recordSeconds: 12, clipSeconds: 8, async (engine, root) =>
        {
            Assert.True(engine.CanExtractClip);
            var sw = Stopwatch.StartNew();
            var clip = await engine.ExtractClipAsync(TimeSpan.FromSeconds(8));
            sw.Stop();

            Assert.Equal(Path.Combine(root, "clips"), Path.GetDirectoryName(clip.FilePath));
            Assert.Matches(@"_clip_\d{8}_\d{6}_8s\.mp4$", Path.GetFileName(clip.FilePath));
            Assert.True(File.Exists(clip.FilePath));
            Assert.Equal(RecordingState.Recording, engine.State);                          // la grabación no se ha enterado

            var (duration, keyframe, packets, frames) = await InspectAsync(new FfmpegLocator(TestAssets.FfmpegDir!).FfprobePath, clip.FilePath);
            Assert.InRange(duration, 8.0, 8.0 + 1.0 + 0.5);                                 // lo pedido + hasta un GOP (1 s a 25 fps) por delante
            Assert.True(keyframe, "el clip debe empezar en un fotograma clave (copia sin recodificar)");
            Assert.Equal(packets, frames);                                                  // decodifica entero: sin paquetes truncados
            Assert.InRange(clip.Duration.TotalSeconds, 8.0, 9.5);
            Assert.True(clip.SizeBytes > 500_000, $"clip de {clip.SizeBytes} bytes");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"tardó {sw.Elapsed.TotalSeconds:0.0} s");
            // El clip no se cuela como segmento de la grabación: vive en su subcarpeta, no junto al archivo.
            Assert.DoesNotContain(Directory.GetFiles(root), f => f.Contains("_clip_"));
        });

    [SkippableFact]
    public Task TheLastSeconds_OfAGrowingTs_BecomeAClip() =>
        RunAsync(ContainerFormat.Ts, null, fragmented: true, recordSeconds: 12, clipSeconds: 8, async (engine, root) =>
        {
            Assert.True(engine.CanExtractClip);
            var clip = await engine.ExtractClipAsync(TimeSpan.FromSeconds(8));
            Assert.EndsWith("_8s.ts", clip.FilePath);
            var (duration, keyframe, packets, frames) = await InspectAsync(new FfmpegLocator(TestAssets.FfmpegDir!).FfprobePath, clip.FilePath);
            Assert.InRange(duration, 7.8, 9.8);
            Assert.True(keyframe, "el clip TS debe empezar en un fotograma clave");
            Assert.Equal(packets, frames);
            Assert.Equal(RecordingState.Recording, engine.State);
        });

    [SkippableFact]
    public Task WithASegmentedRecording_TheClipSpansSegments() =>
        RunAsync(ContainerFormat.Mp4, new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromSeconds(4) },
            fragmented: true, recordSeconds: 13, clipSeconds: 9, async (engine, root) =>
        {
            Assert.True(Directory.GetFiles(root, "*.mp4").Length >= 3, "el banco debería tener al menos 3 segmentos");
            var clip = await engine.ExtractClipAsync(TimeSpan.FromSeconds(9));
            var (duration, keyframe, packets, frames) = await InspectAsync(new FfmpegLocator(TestAssets.FfmpegDir!).FfprobePath, clip.FilePath);
            Assert.InRange(duration, 9.0, 9.0 + 1.0 + 0.5);
            Assert.True(keyframe);
            Assert.Equal(packets, frames);
            Assert.Equal(RecordingState.Recording, engine.State);
        });

    [SkippableFact]
    public async Task WithoutARecording_OrWithAStandardMp4_ThereIsNoClip()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-clip-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = ClipSource();
            await source.OpenAsync();
            // MP4 ESTÁNDAR (moov al final): no se puede leer hasta que se cierra → el botón no se ofrece y la petición se rechaza.
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot, FragmentedMp4 = false };
            await engine.StartPreviewAsync(source, Profile(ContainerFormat.Mp4), "CLP");
            Assert.False(engine.CanExtractClip);
            var idle = await Assert.ThrowsAsync<ClipExtractionException>(() => engine.ExtractClipAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(ClipError.NotRecording, idle.Error);

            await engine.StartRecordingAsync(Guid.NewGuid(), Profile(ContainerFormat.Mp4));
            await Task.Delay(3000);
            Assert.False(engine.CanExtractClip);
            var standard = await Assert.ThrowsAsync<ClipExtractionException>(() => engine.ExtractClipAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(ClipError.UnsupportedContainer, standard.Error);
            Assert.Equal("unsupported-container", standard.Code);
            await engine.StopRecordingAsync();
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
