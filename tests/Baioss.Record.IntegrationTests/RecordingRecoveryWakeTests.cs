using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Preview;
using Baioss.Record.IntegrationTests.Fakes;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Recuperación de la grabación tras la caída del proceso, con el motor REAL y una fuente que se auto-reporta (como el relé
/// DeckLink o NDI) cuya señal decide el test: si la grabación cayó SIN señal, el reintento sale en cuanto la fuente avisa de
/// que la señal volvió (antes esperaba su backoff entero: medido en una Duo 2, 3,3 s de hueco en vez de ~1,3 s); si los
/// intentos fallan CON señal, los avisos de la fuente no los relanzan en bucle.
/// </summary>
public sealed class RecordingRecoveryWakeTests
{
    internal static RecordingProfile Profile() => new()
    {
        Name = "rw", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(1), GopSize = 25,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
        SlateOnSignalLoss = false, // sin carta de ajuste: la vuelta la gestiona la recuperación
    };

    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Esperando: " + what);
            await Task.Delay(20);
        }
    }

    [SkippableFact]
    public async Task ARecordingThatDiedWithoutSignal_IsRebuiltAsSoonAsTheSignalReturns_NotAtTheNextBackoff()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-recovery-wake-{Guid.NewGuid():N}");
        var source = new SyntheticLiveSource();
        try
        {
            await using (var engine = new FfmpegChannelEngine(new FfmpegLocator(TestAssets.FfmpegDir!), NullLogger<FfmpegChannelEngine>.Instance) { OutputRoot = outputRoot })
            {
                await engine.StartPreviewAsync(source, Profile(), "RW");
                await engine.StartRecordingAsync(Guid.NewGuid(), Profile());
                await WaitUntilAsync(() => engine.Stats.FrameCount > 25, TimeSpan.FromSeconds(20), "grabando");

                // La fuente pierde la señal y el proceso de grabación cae (como al morir el proceso de captura de la tarjeta).
                string? firstPiece = engine.LastOutputFile;
                source.Set(SignalState.NoSignal);
                int before = source.Builds;
                Assert.True(engine.KillRecorderProcessForTest(), "no había proceso de grabación que matar");

                // El primer reintento sale al cumplir su backoff (1 s) y falla: aún no hay señal.
                await WaitUntilAsync(() => source.Builds > before, TimeSpan.FromSeconds(5), "el primer reintento");
                await Task.Delay(400); // la señal vuelve a mitad del backoff siguiente (2 s)
                Assert.Equal(before + 1, source.Builds);
                double back = source.Clock.Elapsed.TotalSeconds;
                source.Set(SignalState.Locked);

                await WaitUntilAsync(() => source.Builds > before + 1, TimeSpan.FromSeconds(5), "el reintento con señal");
                double rebuilt; lock (source.BuildTimes) rebuilt = source.BuildTimes[^1];
                Assert.True(rebuilt - back < 0.6, $"El reintento salió {rebuilt - back:0.00} s después de volver la señal (esperó su backoff).");
                // …y la grabación sigue en una pieza nueva.
                await WaitUntilAsync(() => engine.LastOutputFile is { } f && f != firstPiece && File.Exists(f) && new FileInfo(f).Length > 0,
                    TimeSpan.FromSeconds(10), "la pieza nueva graba");
                Assert.Equal(RecordingState.Recording, engine.State);
                await engine.StopRecordingAsync();
            }
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task WhenRebuildsKeepFailingWithSignal_TheSourceAnnouncementsDoNotRetryInALoop()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-recovery-wake-{Guid.NewGuid():N}");
        var source = new SyntheticLiveSource();
        try
        {
            await using (var engine = new FfmpegChannelEngine(new FfmpegLocator(TestAssets.FfmpegDir!), NullLogger<FfmpegChannelEngine>.Instance) { OutputRoot = outputRoot })
            {
                await engine.StartPreviewAsync(source, Profile(), "RW");
                await engine.StartRecordingAsync(Guid.NewGuid(), Profile());
                await WaitUntilAsync(() => engine.Stats.FrameCount > 25, TimeSpan.FromSeconds(20), "grabando");

                // Con señal, pero reconstruir falla por otra cosa; y la fuente no para de anunciar que tiene señal.
                source.Broken = true;
                int before = source.Builds;
                Assert.True(engine.KillRecorderProcessForTest(), "no había proceso de grabación que matar");
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(3.6))
                {
                    source.Set(SignalState.Locked);
                    await Task.Delay(50);
                }

                // Backoff de siempre: reintentos a ~1 s y ~3 s (el siguiente, a los 7 s). Con un reintento por aviso serían decenas.
                int attempts = source.Builds - before;
                Assert.InRange(attempts, 1, 3);
            }
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }
}
