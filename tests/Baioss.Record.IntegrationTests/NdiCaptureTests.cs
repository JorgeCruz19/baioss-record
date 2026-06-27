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
/// Captura NDI nativa end-to-end: una fuente NDI real (el Test Pattern de NDI Tools) entra por el
/// <see cref="NdiCaptureSource"/> (recepción SDK → sockets TCP) y atraviesa el pipeline FFmpeg
/// (<see cref="FfmpegChannelEngine"/>) hasta producir frames de preview y un archivo grabado. Se omite si
/// el runtime NDI no está o no hay una fuente NDI emitiendo.
/// </summary>
public sealed class NdiCaptureTests
{
    [SkippableFact]
    public async Task NdiSource_FlowsThroughPipeline_ToPreviewAndRecording()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        Skip.IfNot(NdiRuntime.IsAvailable, "Runtime NDI no disponible (instala NDI Tools/Runtime).");

        var name = NdiRuntime.DiscoverSources(4000).FirstOrDefault(s => s.Contains("Test Pattern", StringComparison.OrdinalIgnoreCase));
        Skip.If(name is null, "Sin fuente NDI 'Test Pattern' en la red (lánzala con NDI Tools para esta prueba).");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-ndi-{Guid.NewGuid():N}");
        var source = new NdiCaptureSource(new InputSource { Name = name!, Type = InputType.Ndi, Uri = name }, NullLogger.Instance);
        try
        {
            // 1) Abrir la fuente NDI: conecta el receptor y resuelve la resolución/tasa reales.
            await source.OpenAsync();
            Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
            Assert.True(source.CurrentSignal.Resolution is { Width: >= 320 }, "La señal NDI debe traer resolución.");
            Assert.Equal(1, source.AudioInputIndex); // audio en la entrada 1 (sockets separados)
            // El Test Pattern es 8-bit sin alfa → NDI entrega UYVY (YUV 4:2:2, la mitad de bytes que BGRA y
            // swscale barato). Confirma que la optimización de CPU está activa (no caímos al camino bgra).
            Assert.Equal("uyvy422", source.VideoPixelFormat);

            // 2) Pipeline FFmpeg de preview + grabación sobre la fuente NDI.
            var profile = new RecordingProfile
            {
                Name = "ndi", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };
            await using var engine = new FfmpegChannelEngine(new FfmpegLocator(TestAssets.FfmpegDir!), NullLogger.Instance) { OutputRoot = outputRoot };
            int frames = 0;
            engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);

            await engine.StartPreviewAsync(source, profile, "NDI");
            await WaitForAsync(() => Volatile.Read(ref frames) >= 3, TimeSpan.FromSeconds(25));
            Assert.True(Volatile.Read(ref frames) >= 1, "El preview debe recibir frames de la fuente NDI.");

            await engine.StartRecordingAsync(Guid.NewGuid(), profile, baseName: "ndi_test");
            await Task.Delay(TimeSpan.FromSeconds(3));
            await engine.StopRecordingAsync();

            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), $"No se generó archivo NDI: {file}");
            Assert.True(new FileInfo(file!).Length > 0, "El archivo NDI está vacío.");
            var ffprobe = new FfmpegLocator(TestAssets.FfmpegDir!).FfprobePath;
            Assert.Equal("h264", await ProbeCodecAsync(ffprobe, file!));
            // Nota: NO se asercia la DURACIÓN/sincronía de las pistas aquí. La sincronía A/V depende de que la
            // fuente fluya a su tasa real; con un generador NDI «throttled» en segundo plano (caso típico del Test
            // Pattern de NDI Tools, que cae a ~1 fps sin foco) el vídeo grabado sale demasiado corto y haría
            // FALSO-NEGATIVO. La sincronía se verificó de forma determinista fuera de NDI (banco de pruebas con
            // generador paced: sin wallclock, vídeo y audio quedan cuadrados).
        }
        finally
        {
            await source.DisposeAsync();
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout) await Task.Delay(100);
    }

    private static async Task<string> ProbeCodecAsync(string ffprobePath, string file)
    {
        for (int attempt = 0; ; attempt++)
        {
            var psi = new ProcessStartInfo { FileName = ffprobePath, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
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
