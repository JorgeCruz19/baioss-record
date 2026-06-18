using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>El motor de preview decodifica la fuente a frames BGRA y mide el audio de la señal en vivo.</summary>
public sealed class PreviewEngineTests
{
    [SkippableFact]
    public async Task Preview_ProducesVideoFramesAndAudioLevels()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");

        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var source = new FileCaptureSource(new InputSource
        {
            Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
            Parameters = { ["loop"] = "1", ["realtime"] = "1" },
            ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
        });
        await source.OpenAsync();

        await using var engine = new FfmpegPreviewEngine(locator, NullLogger<FfmpegPreviewEngine>.Instance);

        int frames = 0, audioEvents = 0, badFrames = 0;
        double lastLeft = double.NaN, lastRight = double.NaN;
        int expectedBytes = engine.FrameWidth * engine.FrameHeight * 4;
        engine.FrameReady += (_, f) => { Interlocked.Increment(ref frames); if (f.Bgra.Length != expectedBytes) Interlocked.Increment(ref badFrames); };
        engine.AudioPeaksUpdated += (_, lr) => { Interlocked.Increment(ref audioEvents); lastLeft = lr.Left; lastRight = lr.Right; };

        await engine.StartAsync(source);
        await Task.Delay(TimeSpan.FromSeconds(4));
        await engine.StopAsync();

        Assert.True(frames >= 25, $"Pocos frames de preview: {frames}");
        Assert.Equal(0, badFrames);                              // todos del tamaño BGRA esperado
        Assert.True(audioEvents >= 3, $"Pocos eventos de audio: {audioEvents}");
        Assert.InRange(lastLeft, -100.0, 0.0);                   // dBFS plausible
        Assert.InRange(lastRight, -100.0, 0.0);
    }
}
