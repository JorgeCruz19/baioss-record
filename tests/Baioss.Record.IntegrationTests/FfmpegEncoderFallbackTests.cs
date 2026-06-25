using System.Diagnostics;
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
/// Verifica el FALLBACK AUTOMÁTICO de codificador del <see cref="FfmpegChannelEngine"/> end-to-end: si el
/// codificador de vídeo de grabación no logra ABRIR (codificador por hardware no disponible —el caso real
/// es agotar las sesiones NVENC con varios canales—), el canal degrada al siguiente de la cadena y SIGUE
/// grabando, en lugar de quedarse sin archivo. Se provoca el fallo configurando un perfil con un
/// codificador por hardware que el equipo no puede abrir; el test se omite si ese codificador SÍ abre.
/// </summary>
public sealed class FfmpegEncoderFallbackTests
{
    [SkippableFact]
    public async Task Recording_WhenHardwareEncoderCannotOpen_FallsBackToCpuAndProducesPlayableFile()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");

        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        // El fallback solo puede provocarse si el codificador por hardware elegido NO abre en este equipo.
        // h264_amf (AMD AMF) falla en equipos sin GPU AMD: caso ideal y reproducible. Si SÍ abriera (hay
        // AMD), AMF grabaría h264 directamente y no habría nada que degradar → se omite el test.
        var (amfUsable, _) = await locator.ProbeVideoEncoderAsync("h264_amf");
        Skip.If(amfUsable, "h264_amf abre en este equipo; no se puede forzar el fallback con AMF.");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-fb-{Guid.NewGuid():N}");
        try
        {
            var source = new FileCaptureSource(new InputSource
            {
                Name = "clip", Type = InputType.File, Uri = TestAssets.Clip!,
                Parameters = { ["loop"] = "1", ["realtime"] = "1" },
                ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
            });
            await source.OpenAsync();

            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };

            // Señal del fallback: la alarma EncoderFallback pasa a activa SOLO cuando el motor degrada.
            var fellBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.AlarmChanged += (_, a) => { if (a.Type == AlarmType.EncoderFallback && a.Active) fellBack.TrySetResult(); };

            // Tras el fallback, el preview/grabación debe SEGUIR fluyendo (proceso libx264 vivo).
            int framesAfterFallback = 0;
            engine.FrameReady += (_, _) => { if (fellBack.Task.IsCompleted) Interlocked.Increment(ref framesAfterFallback); };

            // Preview siempre activo (no graba): perfil base por CPU.
            var basePf = new RecordingProfile
            {
                Name = "preview", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };
            await engine.StartPreviewAsync(source, basePf, "FBK");

            // Grabación pedida con AMF (no abre) → debe degradar a libx264 y grabar igual.
            var recPf = new RecordingProfile
            {
                Name = "rec-amf", VideoCodec = VideoCodec.H264Amf, HwAccel = HwAccel.Amf,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };
            await engine.StartRecordingAsync(Guid.NewGuid(), recPf, baseName: "fallback_amf");

            // 1) El motor detectó el fallo de apertura y activó el fallback.
            var firedFallback = await Task.WhenAny(fellBack.Task, Task.Delay(TimeSpan.FromSeconds(30))) == fellBack.Task;
            Assert.True(firedFallback, "No se activó el fallback de codificador ante AMF no disponible.");

            // 2) El pipeline sigue vivo tras el fallback (llegan frames del proceso degradado).
            await WaitForAsync(() => Volatile.Read(ref framesAfterFallback) >= 1, TimeSpan.FromSeconds(20));
            Assert.True(Volatile.Read(ref framesAfterFallback) >= 1, "El preview debe seguir fluyendo tras el fallback.");

            await Task.Delay(TimeSpan.FromSeconds(2)); // graba ~2 s con el codificador degradado
            await engine.StopRecordingAsync();

            // 3) El archivo existe, no está vacío y es un H.264 legible (libx264 tras degradar desde AMF).
            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), $"No se generó el archivo: {file}");
            Assert.True(new FileInfo(file!).Length > 0, "El archivo de grabación está vacío.");
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
