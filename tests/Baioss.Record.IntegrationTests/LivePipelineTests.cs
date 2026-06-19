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
