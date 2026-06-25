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
/// Verifica la red de seguridad de integridad: <see cref="FfmpegLocator.ProbeMediaAsync"/> distingue un
/// archivo reproducible de uno corrupto/vacío, y una grabación BUENA no levanta la alarma
/// RecordingUnverified (sin falsos positivos).
/// </summary>
public sealed class RecordingVerificationTests
{
    [SkippableFact]
    public async Task ProbeMedia_DistinguishesPlayableFromCorrupt()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);

        // Archivo real (el clip de prueba) → reproducible, con pista de vídeo y duración.
        var good = await locator.ProbeMediaAsync(TestAssets.Clip!);
        Assert.True(good.IsPlayable, "El clip de prueba debe ser reproducible.");
        Assert.True(good.HasVideo);
        Assert.True(good.DurationSeconds > 0);

        // Archivo basura con extensión .mp4 → NO reproducible (simula un contenedor dañado / sin moov).
        var garbage = Path.Combine(Path.GetTempPath(), $"baioss-garbage-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(garbage, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        try { Assert.False((await locator.ProbeMediaAsync(garbage)).IsPlayable); }
        finally { File.Delete(garbage); }

        // Archivo inexistente → no reproducible.
        var missing = Path.Combine(Path.GetTempPath(), $"baioss-missing-{Guid.NewGuid():N}.mp4");
        Assert.False((await locator.ProbeMediaAsync(missing)).IsPlayable);
    }

    [SkippableFact]
    public async Task GoodRecording_DoesNotRaiseUnverifiedAlarm()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-verify-{Guid.NewGuid():N}");
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
                Name = "verify", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };

            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            bool unverified = false;
            engine.AlarmChanged += (_, a) => { if (a.Type == AlarmType.RecordingUnverified && a.Active) unverified = true; };

            await engine.StartPreviewAsync(source, profile, "VER");
            await engine.StartRecordingAsync(Guid.NewGuid(), profile, baseName: "good");
            await Task.Delay(TimeSpan.FromSeconds(3));
            await engine.StopRecordingAsync();
            await Task.Delay(TimeSpan.FromSeconds(1)); // deja correr la verificación (Task.Delay(300) + ffprobe)

            Assert.False(unverified, "Una grabación buena NO debe levantar RecordingUnverified.");
            var probe = await locator.ProbeMediaAsync(engine.LastOutputFile!);
            Assert.True(probe.IsPlayable, "El archivo grabado debe ser reproducible.");
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
