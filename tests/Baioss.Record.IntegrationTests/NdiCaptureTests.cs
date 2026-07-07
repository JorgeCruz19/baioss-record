using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

    /// <summary>
    /// #19/#35/A3/A4 — DERIVA A/V con una fuente NDI REAL a tasa completa (OBS): graba ~15 s y mide la diferencia
    /// de DURACIÓN y de start_time entre las pistas de vídeo y audio del MP4. Con el Test Pattern (throttled) esto
    /// daba falso-negativo; con OBS a tasa real es la medida que faltaba. Escribe un informe a
    /// %TEMP%/baioss-ndi-avsync.txt (siempre, antes de aseverar) para poder inspeccionar los números.
    /// </summary>
    [SkippableFact]
    public async Task NdiRealSource_MeasuresAvSync()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        Skip.IfNot(NdiRuntime.IsAvailable, "Runtime NDI no disponible (instala NDI Tools/Runtime).");
        var sources = NdiRuntime.DiscoverSources(4000);
        Skip.If(sources.Count == 0, "Sin fuentes NDI en la red (arranca la salida NDI de OBS).");
        var name = sources.FirstOrDefault(s => s.Contains("OBS", StringComparison.OrdinalIgnoreCase)) ?? sources[0];

        var report = Path.Combine(Path.GetTempPath(), "baioss-ndi-avsync.txt");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-ndi-{Guid.NewGuid():N}");
        var source = new NdiCaptureSource(new InputSource { Name = name, Type = InputType.Ndi, Uri = name }, NullLogger.Instance);
        try
        {
            await source.OpenAsync();
            var sig = source.CurrentSignal;
            string resText = sig.Resolution is { } r ? $"{r.Width}x{r.Height}" : "?";
            string rateText = sig.FrameRate is { } fr ? fr.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?";

            const int recordSeconds = 20;
            var profile = new RecordingProfile
            {
                // NVENC como la app real (el x264 por CPU en un portátil satura y falsea la medida con drops).
                Name = "ndi-sync", VideoCodec = VideoCodec.H264Nvenc, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(6), GopSize = 50,
                AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
            };
            await using var engine = new FfmpegChannelEngine(new FfmpegLocator(TestAssets.FfmpegDir!), NullLogger.Instance) { OutputRoot = outputRoot };
            int previewFrames = 0;
            engine.FrameReady += (_, _) => Interlocked.Increment(ref previewFrames); // ¿fluye el PREVIEW? (0 = congelado)
            await engine.StartPreviewAsync(source, profile, "NDI");
            await Task.Delay(TimeSpan.FromSeconds(2)); // deja establecer el preview/recepción antes de grabar
            await engine.StartRecordingAsync(Guid.NewGuid(), profile, baseName: "ndi_sync");
            await Task.Delay(TimeSpan.FromSeconds(recordSeconds));
            await engine.StopRecordingAsync();

            var file = engine.LastOutputFile;
            Assert.True(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, $"No se generó archivo NDI: {file}");
            var ffprobe = new FfmpegLocator(TestAssets.FfmpegDir!).FfprobePath;
            var (vDur, vStart, aDur, aStart, vFrames) = await ProbeStreamsAsync(ffprobe, file!);
            double durDrift = vDur - aDur;
            double startOffset = vStart - aStart;
            double deliveredFps = recordSeconds > 0 ? vFrames / (double)recordSeconds : 0;
            // La duración por sí sola NO detecta un vídeo CONGELADO (mismo frame duplicado → duración correcta pero
            // imagen estática). freezedetect mide cuántos segundos del clip están congelados.
            double frozenSec = await FrozenSecondsAsync(new FfmpegLocator(TestAssets.FfmpegDir!).FfmpegPath, file!);
            int pFrames = Volatile.Read(ref previewFrames);
            double previewFps = pFrames / (double)(recordSeconds + 2);

            string I(double d, string fmt) => d.ToString(fmt, CultureInfo.InvariantCulture);
            var lines = new[]
            {
                $"fuente         : {name}",
                $"otras fuentes  : {string.Join(" | ", sources.Where(s => s != name))}",
                $"señal          : {sig.State} {resText} @ {rateText} fps · pix {source.VideoPixelFormat}",
                $"encoder        : {profile.VideoCodec}",
                $"archivo        : {Path.GetFileName(file)} ({new FileInfo(file!).Length / 1024 / 1024} MB)",
                $"ventana grab.  : {recordSeconds}s",
                $"vídeo          : dur={I(vDur, "0.000")}s frames={vFrames} → {I(deliveredFps, "0.#")} fps entregados (esperado ~{rateText}) start={I(vStart, "0.000")}s",
                $"audio          : dur={I(aDur, "0.000")}s start={I(aStart, "0.000")}s",
                $"DERIVA dur     : {I(durDrift, "+0.000;-0.000")}s (vídeo - audio; ~0 = sincronizado)",
                $"OFFSET inicial : {I(startOffset, "+0.000;-0.000")}s (#19)",
                $"CONGELADO grab : {I(frozenSec, "0.0")}s de {I(vDur, "0.0")}s (freezedetect; ~0 = hay movimiento)",
                $"PREVIEW        : {pFrames} frames → {I(previewFps, "0.#")} fps (0 = preview CONGELADO)",
            };
            File.WriteAllText(report, string.Join(Environment.NewLine, lines));

            // Umbral holgado: 15 s de grabación no deberían derivar más de ~0,5 s si el pipeline está sano.
            Assert.True(Math.Abs(durDrift) < 0.5,
                "Deriva A/V fuera de umbral:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }
        finally
        {
            await source.DisposeAsync();
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Duración, start_time (s) y nº de frames de vídeo de un MP4, vía ffprobe JSON.</summary>
    private static async Task<(double VDur, double VStart, double ADur, double AStart, long VFrames)> ProbeStreamsAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo { FileName = ffprobe, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,duration,start_time,nb_frames", "-of", "json", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var json = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        double vDur = 0, vStart = 0, aDur = 0, aStart = 0; long vFrames = 0;
        using var doc = JsonDocument.Parse(json);
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            string? type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            double dur = Num(s, "duration"), start = Num(s, "start_time");
            if (type == "video") { vDur = dur; vStart = start; vFrames = (long)Num(s, "nb_frames"); }
            else if (type == "audio") { aDur = dur; aStart = start; }
        }
        return (vDur, vStart, aDur, aStart, vFrames);

        static double Num(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>Segundos de vídeo CONGELADO (freezedetect): pilla un vídeo estático que la duración no delata.</summary>
    private static async Task<double> FrozenSecondsAsync(string ffmpeg, string file)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-i", file, "-vf", "freezedetect=n=-60dB:d=0.5", "-map", "0:v:0", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        double sum = 0;
        const string key = "freeze_duration:";
        foreach (var line in err.Split('\n'))
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i >= 0 && double.TryParse(line[(i + key.Length)..].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                sum += d;
        }
        return sum;
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
