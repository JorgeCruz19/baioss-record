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
/// Grabación robusta con archivos normales: cada segmento se escribe fragmentado (una caída pierde ≤ 1 s, no la pieza) y,
/// al cerrarse, se finaliza en segundo plano como MP4 estándar con el índice al inicio (duración visible, búsqueda
/// exacta). Mientras se graba, el segmento en curso sigue fragmentado (y de él se pueden sacar clips). El renombrado al
/// detener espera a las finalizaciones en vuelo. (Opción 4 del incidente 2026-09-06.)
/// </summary>
public sealed class SegmentFinalizeTests
{
    private static RecordingProfile SegmentedProfile(int seconds) => new()
    {
        Name = "fin", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(3), GopSize = 25,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
        Segmentation = new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromSeconds(seconds) },
    };

    private static FileCaptureSource ClipSource() => new(new InputSource
    {
        Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
        Parameters = { ["loop"] = "1", ["realtime"] = "1" },
        ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
    });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException(what);
            await Task.Delay(250);
        }
    }

    [SkippableFact]
    public async Task ClosedSegments_AreFinalizedToStandardMp4_WhileTheCurrentOneStaysFragmented_AndAllEndUpNormal()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-finalize-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = ClipSource();
            await source.OpenAsync();
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot, FragmentedMp4 = true };
            await engine.StartPreviewAsync(source, SegmentedProfile(3), "FIN");
            await engine.StartRecordingAsync(Guid.NewGuid(), SegmentedProfile(3));

            // A los ~10 s hay ≥ 3 segmentos: los cerrados van quedando finalizados; el que se escribe sigue fragmentado.
            await Task.Delay(10500);
            string[] Files() => Directory.GetFiles(outputRoot, "FIN_*.mp4").OrderBy(f => f).ToArray();
            await WaitUntilAsync(() => Files().Length >= 3, TimeSpan.FromSeconds(15), "no hay 3 segmentos");
            var during = Files();
            // El segmento en curso NO está finalizado: fragmentado, o recién creado (solo ftyp+moov vacío, aún sin fragmentos).
            Assert.False(Mp4Layout.Inspect(during[^1]).IsFinalized, "el segmento en curso no debe estar finalizado (se escribe fragmentado)");
            await WaitUntilAsync(() => Mp4Layout.Inspect(during[0]).IsFinalized, TimeSpan.FromSeconds(20), "el primer segmento cerrado no se finalizó");
            Assert.Equal(RecordingState.Recording, engine.State); // la finalización en segundo plano no toca la grabación

            await engine.StopRecordingAsync();
            // Tras detener, TODAS las piezas acaban normales (índice al inicio, sin fragmentos), reproducibles, sin temporales.
            var all = Files();
            Assert.True(all.Length >= 3, $"solo {all.Length} segmentos");
            await WaitUntilAsync(() => all.All(f => Mp4Layout.Inspect(f).IsFinalized), TimeSpan.FromSeconds(40), "no se finalizaron todos los segmentos: " +
                string.Join(", ", all.Select(f => $"{Path.GetFileName(f)}={(Mp4Layout.Inspect(f).IsFinalized ? "ok" : Mp4Layout.Inspect(f).Fragmented ? "fMP4" : "?")}")));
            foreach (var f in all)
            {
                var probe = await locator.ProbeMediaAsync(f);
                Assert.True(probe.IsPlayable && probe.DurationSeconds > 1, $"{Path.GetFileName(f)} no se reproduce ({probe.DurationSeconds:0.0} s)");
            }
            await WaitUntilAsync(() => Directory.GetFiles(outputRoot, "*" + FfmpegLocator.RemuxTempSuffix).Length == 0, TimeSpan.FromSeconds(10), "quedó un temporal de remux");
            Assert.Equal(all.Length, Files().Length); // los temporales nunca se contaron como segmentos

            // El renombrado al detener espera a las finalizaciones y mueve las piezas ya normales.
            var pairs = engine.RenameSessionFiles("Final");
            Assert.Equal(all.Length, pairs.Count);
            foreach (var (_, renamed) in pairs)
            {
                Assert.True(File.Exists(renamed), renamed);
                Assert.True(Mp4Layout.Inspect(renamed).IsFinalized, Path.GetFileName(renamed));
            }
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task ASingleFileRecording_StillEndsUpAsAStandardMp4_AfterStop()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-finalize-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = ClipSource();
            await source.OpenAsync();
            var profile = SegmentedProfile(3); profile.Segmentation = null;
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot, FragmentedMp4 = true };
            await engine.StartPreviewAsync(source, profile, "FIN");
            await engine.StartRecordingAsync(Guid.NewGuid(), profile);
            await Task.Delay(4000);
            var file = engine.LastOutputFile!;
            Assert.True(Mp4Layout.Inspect(file).Fragmented, "mientras graba, fragmentado");
            await engine.StopRecordingAsync();
            await WaitUntilAsync(() => Mp4Layout.Inspect(file).IsFinalized, TimeSpan.FromSeconds(30), "el archivo único no se finalizó");
            Assert.True((await locator.ProbeMediaAsync(file)).IsPlayable);
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
