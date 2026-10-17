using System.Diagnostics;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Preview;
using Baioss.Record.IntegrationTests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Detener la grabación de una fuente con el vídeo y el audio en DOS entradas, como NDI (<see cref="SplitInputLiveSource"/>), con
/// el motor y el FFmpeg reales y sin el runtime NDI: el archivo conserva el final del audio. FFmpeg lee el audio por detrás del
/// vídeo —su planificador lo frena para no adelantarse a lo que sale del codificador de vídeo, que retiene cerca de un segundo—
/// y con la «q» deja de leer en el acto: el audio que esperaba en el socket se perdía y el archivo acababa con el audio 0,1–0,9 s
/// más corto que el vídeo. Ahora la fuente le cierra las dos entradas en el mismo instante y FFmpeg termina solo con todo dentro.
/// </summary>
public sealed class SplitInputStopTests
{
    private readonly ITestOutputHelper _out;
    public SplitInputStopTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task StoppingARecording_KeepsTheEndOfTheAudio_WhenAudioComesInItsOwnInput()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-split-stop-{Guid.NewGuid():N}");
        var log = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (log) log.Add(l); });
        var profile = new RecordingProfile
        {
            Name = "split", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(4), GopSize = 30,
            OutputFrameRate = FrameRate.P2997,
            AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
        };
        await using var source = new SplitInputLiveSource();
        try
        {
            int previewFrames = 0;
            await using var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine")) { OutputRoot = outputRoot };
            engine.FrameReady += (_, _) => Interlocked.Increment(ref previewFrames);
            await engine.StartPreviewAsync(source, profile, "SPLIT");
            await RecordingRecoveryWakeTests.WaitUntilAsync(() => Volatile.Read(ref previewFrames) > 15, TimeSpan.FromSeconds(20), "preview");

            await engine.StartRecordingAsync(Guid.NewGuid(), profile, baseName: "split");
            await Task.Delay(4000);
            var stop = Stopwatch.StartNew();
            await engine.StopRecordingAsync();
            stop.Stop();
            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, $"Sin archivo: {file}");
            var (vDur, aDur) = await DurationsAsync(locator.FfprobePath, file!);
            string[] warnings;
            lock (log) warnings = log.Where(l => l.StartsWith("[Warning]", StringComparison.Ordinal) || l.StartsWith("[Error]", StringComparison.Ordinal)).ToArray();
            _out.WriteLine($"Detener {stop.Elapsed.TotalSeconds:0.00} s · vídeo {vDur:0.000} s · audio {aDur:0.000} s · audio−vídeo {aDur - vDur:+0.000;-0.000} s");
            foreach (var w in warnings) _out.WriteLine(w);

            Assert.InRange(vDur, 3.5, 5.0);
            // Con la «q», el audio acababa 0,1–0,9 s antes que el vídeo. Ahora llega hasta el mismo instante: le queda el desfase del
            // arranque (FFmpeg conecta la entrada de audio unos ms después que la de vídeo) y el relleno del último bloque AAC.
            Assert.True(aDur > vDur - 0.1, $"El audio acaba {vDur - aDur:0.000} s antes que el vídeo (vídeo {vDur:0.000} s, audio {aDur:0.000} s).");
            Assert.True(aDur < vDur + 0.15, $"El audio dura {aDur - vDur:0.000} s más que el vídeo (vídeo {vDur:0.000} s, audio {aDur:0.000} s).");
            Assert.DoesNotContain(warnings, w => w.Contains("tras cerrarle la entrada", StringComparison.Ordinal)); // terminó solo, sin la «q»
            Assert.True(stop.Elapsed < TimeSpan.FromSeconds(3), $"Detener tardó {stop.Elapsed.TotalSeconds:0.0} s.");
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }

    private static async Task<(double Video, double Audio)> DurationsAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,duration", "-of", "csv=p=0", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string s = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        double video = double.NaN, audio = double.NaN;
        foreach (var line in s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) continue;
            if (parts[0] == "video") video = d; else if (parts[0] == "audio") audio = d;
        }
        return (video, audio);
    }
}
