using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Preview;
using Baioss.Record.IntegrationTests.Fakes;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Una grabación segmentada con fin previsto (como una programada con duración), con el motor y el FFmpeg REALES: si la
/// duración es múltiplo del segmento y el Detener llega un instante después del fin, no queda un último segmento ridículo
/// (medido con una DeckLink: 30 min en segmentos de 10 dieron 3 × 600 s y uno de 0,6 s), y no se pierde nada.
/// </summary>
public sealed class SegmentEndAlignmentRecordingTests
{
    private static async Task<double> DurationAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string s = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    [SkippableFact]
    public async Task AStopJustAfterAPlannedEndOnACut_LeavesNoTinyTrailingSegment_AndLosesNothing()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-segment-end-{Guid.NewGuid():N}");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var source = new SyntheticLiveSource();
        try
        {
            // Segmentos de 4 s con un fotograma clave por segundo (los cortes caen justo en 4 y 8 s) y fin previsto a 12 s: tres
            // segmentos exactos, como 30 min en segmentos de 10. (Con segmentos más cortos la tolerancia —un cuarto de segmento—
            // no cubriría lo que tarda en arrancar el proceso en una máquina cargada.)
            var profile = RecordingRecoveryWakeTests.Profile();
            profile.Segmentation = new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromSeconds(4) };
            double recorded;
            await using (var engine = new FfmpegChannelEngine(locator, NullLogger<FfmpegChannelEngine>.Instance) { OutputRoot = outputRoot })
            {
                await engine.StartPreviewAsync(source, profile, "SE");
                await RecordingRecoveryWakeTests.WaitUntilAsync(() => engine.Stats.FrameCount > 10, TimeSpan.FromSeconds(20), "preview en marcha");

                var plannedEnd = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
                profile.Segmentation.PlannedEnd = plannedEnd;
                var sw = Stopwatch.StartNew();
                await engine.StartRecordingAsync(Guid.NewGuid(), profile, "Programa");
                // El Detener llega un poco DESPUÉS del fin previsto, como el del programador (su tick, el relevo del preview).
                var late = plannedEnd + TimeSpan.FromSeconds(0.8) - DateTimeOffset.UtcNow;
                if (late > TimeSpan.Zero) await Task.Delay(late);
                await engine.StopRecordingAsync();
                recorded = sw.Elapsed.TotalSeconds;
            }

            var files = Directory.GetFiles(outputRoot, "Programa_*.mp4")
                .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f).Split('_')[^1], CultureInfo.InvariantCulture)).ToArray();
            var durations = new List<double>();
            foreach (var f in files) durations.Add(await DurationAsync(locator.FfprobePath, f));
            string seen = string.Join(" + ", durations.Select(d => d.ToString("0.00", CultureInfo.InvariantCulture)));

            // Tres segmentos, ninguno corto: el último absorbe lo que tardó el Detener (sin el fin previsto, este mismo escenario
            // deja un cuarto segmento con lo que pasa del corte de 12 s).
            Assert.True(files.Length == 3, $"{files.Length} segmentos ({seen} s) tras {recorded:0.0} s de grabación.");
            Assert.All(durations, d => Assert.True(d >= 3.0, $"Segmento corto: {seen} s."));
            Assert.True(durations[^1] >= 4.0, $"El último segmento no absorbió el final: {seen} s.");
            // Y nada perdido: entre los tres cubren lo grabado (menos lo que tarda en arrancar el proceso).
            Assert.True(durations.Sum() >= 11.0, $"Faltan segundos: {seen} s tras {recorded:0.0} s de grabación.");
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }
}
