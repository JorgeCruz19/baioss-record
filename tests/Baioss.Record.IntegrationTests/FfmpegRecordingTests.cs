using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>Graba un clip real con el motor FFmpeg y verifica el archivo de salida y su formato.</summary>
public sealed class FfmpegRecordingTests
{
    [SkippableFact]
    public async Task Records_RealClip_ProducesOutputFile()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        await WithRecordingAsync(
            new RecordingProfile
            {
                Name = "test", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            },
            (output, _) =>
            {
                Assert.True(new FileInfo(output).Length > 0, "El archivo de salida está vacío.");
                return Task.CompletedTask;
            });
    }

    [SkippableFact]
    public async Task Records_AtChosenResolution_ScalesOutputFile()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        await WithRecordingAsync(
            new RecordingProfile
            {
                Name = "scaled", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(4), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
                TargetResolution = new Resolution(640, 360),   // el operador eligió 640×360
            },
            async (output, locator) =>
            {
                var (width, height) = await ProbeResolutionAsync(locator.FfprobePath, output);
                Assert.Equal(640, width);
                Assert.Equal(360, height);
            });
    }

    [SkippableFact]
    public async Task Records_Interlaced_ProducesValidH264()
    {
        // Que los flags de entrelazado sean los correctos lo verifica el test unitario del builder
        // (-field_order tt, +ilme+ildct, setfield). El marcado real de campo en el archivo depende de
        // la fuente (una entrada 1080i real); con un clip PROGRESIVO ffprobe lo reporta de forma no
        // determinista. Aquí se verifica lo estable: que el encode entrelazado produce un H.264 válido.
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        await WithRecordingAsync(
            new RecordingProfile
            {
                Name = "interlaced", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
                ScanType = ScanType.InterlacedTff, OutputFrameRate = FrameRate.P25,   // entrelazado TFF
            },
            async (output, locator) =>
            {
                var codec = await ProbeEntryAsync(locator.FfprobePath, output, "codec_name");
                Assert.Equal("h264", codec);
            });
    }

    /// <summary>
    /// Graba el clip de prueba con el perfil dado hasta que el encoder ha producido frames reales
    /// (espera el evento de estadísticas, no un retraso fijo: robusto bajo carga), detiene y deja
    /// verificar el archivo. Limpia el directorio temporal al terminar.
    /// </summary>
    private static async Task WithRecordingAsync(RecordingProfile profile, Func<string, FfmpegLocator, Task> verify)
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-rec-{Guid.NewGuid():N}");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            var source = new FileCaptureSource(new InputSource
            {
                Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
                Parameters = { ["realtime"] = "1" },
                ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
            });
            await source.OpenAsync();

            await using var recorder = new FfmpegRecorderEngine(locator, NullLogger<FfmpegRecorderEngine>.Instance)
            {
                OutputRoot = outputRoot,
                ProxyRoot = Path.Combine(outputRoot, "proxy"),
            };
            var session = new RecordingSession
            {
                ChannelId = Guid.NewGuid(), ProfileId = profile.Id, InputSourceId = source.Definition.Id,
                StartedAt = DateTimeOffset.UtcNow,
            };

            var output = await RecordUntilFramesAsync(recorder, session, profile, source, minFrames: 30);

            Assert.True(output is not null && File.Exists(output), $"No se generó el archivo: {output}");
            await verify(output!, locator);
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<string?> RecordUntilFramesAsync(
        FfmpegRecorderEngine recorder, RecordingSession session, RecordingProfile profile, ICaptureSource source, int minFrames)
    {
        var framesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStats(object? _, RecorderStats stats) { if (stats.FrameCount >= minFrames) framesReady.TrySetResult(); }

        recorder.StatsUpdated += OnStats;
        try
        {
            await recorder.StartAsync(session, profile, source);
            // Espera a que el encoder produzca frames reales (o un tope de seguridad).
            await Task.WhenAny(framesReady.Task, Task.Delay(TimeSpan.FromSeconds(25)));
        }
        finally
        {
            recorder.StatsUpdated -= OnStats;
        }

        await recorder.StopAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // deja cerrar el contenedor
        return recorder.LastOutputFile;
    }

    private static async Task<(int Width, int Height)> ProbeResolutionAsync(string ffprobePath, string file)
    {
        var output = await ProbeEntryAsync(ffprobePath, file, "width,height", "csv=s=x:p=0");
        var parts = output.Split('x');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static Task<string> ProbeEntryAsync(string ffprobePath, string file, string entry)
        => ProbeEntryAsync(ffprobePath, file, entry, "default=nw=1:nk=1");

    private static async Task<string> ProbeEntryAsync(string ffprobePath, string file, string entry, string format)
    {
        // Reintenta ante una lectura vacía transitoria (contenedor recién cerrado).
        for (int attempt = 0; ; attempt++)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", $"stream={entry}", "-of", format, file })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();

            if (output.Length > 0 || attempt >= 3) return output;
            await Task.Delay(400);
        }
    }
}
