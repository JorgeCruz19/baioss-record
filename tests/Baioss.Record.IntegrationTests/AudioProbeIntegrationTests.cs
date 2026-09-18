using System.Diagnostics;
using Baioss.Record.Domain;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// «Medir audio» contra el FFmpeg real: un archivo de 8 canales con tono en los canales 1-6 y silencio en 7-8 debe
/// medirse canal a canal y proponer solo los pares con sonido. Es la misma ruta que usará una DeckLink (sin tarjeta
/// aquí, el tipo File ejercita el parseo y el proceso de punta a punta).
/// </summary>
public sealed class AudioProbeIntegrationTests
{
    [SkippableFact]
    public async Task MeasureAudio_EightChannelFile_ReportsPeakPerChannelAndActivePairs()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg no disponible en tools/.");
        var ffmpeg = Path.Combine(TestAssets.FfmpegDir!, "ffmpeg.exe");
        var clip = Path.Combine(Path.GetTempPath(), $"baioss-probe-{Guid.NewGuid():N}.mov");
        try
        {
            // 2 s de tonos: canales 1-6 con señal (0,4 ≈ −8 dBFS), canales 7-8 en silencio absoluto.
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                "-i", "aevalsrc=0.4*sin(200*2*PI*t)|0.4*sin(300*2*PI*t)|0.4*sin(500*2*PI*t)|0.4*sin(700*2*PI*t)|0.4*sin(1000*2*PI*t)|0.4*sin(1200*2*PI*t)|0|0:c=7.1:s=48000",
                "-t", "2", "-c:a", "pcm_s16le", "-f", "mov", clip,
            }) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { await p.WaitForExitAsync(); Assert.Equal(0, p.ExitCode); }

            var enumerator = new FfmpegDeviceEnumerator(new FfmpegLocator(TestAssets.FfmpegDir!));
            var probe = await enumerator.MeasureAudioAsync(InputType.File, clip, channels: 8);

            Assert.NotNull(probe);
            Assert.Equal(8, probe!.Channels);
            Assert.Equal(8, probe.PeakDb.Count);
            Assert.InRange(probe.PeakDb[4], -9.0, -7.0);                 // canal 5 con tono
            Assert.True(probe.PeakDb[6] < AudioProbe.SilenceDb);          // canal 7 en silencio
            Assert.Equal(new[] { 1, 2, 3 }, probe.ActivePairs);           // pares 1-2, 3-4 y 5-6; el 7-8 callado
        }
        finally
        {
            try { File.Delete(clip); } catch { /* temporal */ }
        }
    }
}
