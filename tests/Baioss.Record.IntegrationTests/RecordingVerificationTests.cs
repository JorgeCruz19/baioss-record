using System.Diagnostics;
using System.Text;
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

            // La grabación quedó OPTIMIZADA para búsqueda: archivo único → faststart (índice al inicio, sin moof).
            await Task.Delay(TimeSpan.FromSeconds(1)); // deja terminar el remux en segundo plano
            var rec = engine.LastOutputFile!;
            Assert.False(ContainsBox(rec, "moof"), "El archivo único debe quedar des-fragmentado tras el remux.");
            int moovOff = BoxOffset(rec, "moov"), mdatOff = BoxOffset(rec, "mdat");
            Assert.True(moovOff >= 0 && moovOff < mdatOff, "El índice (moov) debe ir ANTES del mdat (faststart).");
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task RemuxFaststart_ConvertsFragmentedMp4ToSeekable()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg/clip de prueba no disponibles en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var frag = Path.Combine(Path.GetTempPath(), $"baioss-frag-{Guid.NewGuid():N}.mp4");
        try
        {
            // fMP4 fragmentado igual que graba la app (empty_moov → SIN índice al inicio, solo recuperable por fragmentos).
            await RunFfmpegAsync(locator.FfmpegPath, new[]
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=2",
                "-c:v", "libx264", "-preset", "ultrafast",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "500000", "-y", frag
            });
            Assert.True(ContainsBox(frag, "moof"), "El fMP4 de partida debe estar fragmentado (moof).");

            Assert.True(await locator.RemuxFaststartAsync(frag), "El remux debe reescribir el .mp4.");

            // Resultado: ya NO fragmentado, índice al inicio, y SIGUE siendo reproducible (no se perdió nada).
            Assert.False(ContainsBox(frag, "moof"), "Tras el remux no debe quedar fragmentado.");
            int moovOff = BoxOffset(frag, "moov"), mdatOff = BoxOffset(frag, "mdat");
            Assert.True(moovOff >= 0 && moovOff < mdatOff, "El moov debe ir ANTES del mdat (faststart).");
            Assert.True((await locator.ProbeMediaAsync(frag)).IsPlayable, "El remuxeado debe seguir siendo reproducible.");
        }
        finally { try { File.Delete(frag); } catch { /* best effort */ } }
    }

    [SkippableFact]
    public async Task RemuxFaststart_NoOpForNonMp4()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var mkv = Path.Combine(Path.GetTempPath(), $"baioss-{Guid.NewGuid():N}.mkv");
        await File.WriteAllBytesAsync(mkv, new byte[] { 0, 1, 2, 3 });
        try { Assert.False(await locator.RemuxFaststartAsync(mkv), "faststart no aplica a contenedores que no sean MP4/MOV."); }
        finally { File.Delete(mkv); }
    }

    private static async Task RunFfmpegAsync(string exe, string[] args)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    /// <summary>Busca el fourcc ASCII de un box ISO-BMFF; suficiente para distinguir moof/moov/mdat en archivos de test pequeños.</summary>
    private static int BoxOffset(string path, string box)
    {
        var bytes = File.ReadAllBytes(path);
        var pat = Encoding.ASCII.GetBytes(box);
        for (int i = 0; i <= bytes.Length - pat.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pat.Length; j++) if (bytes[i + j] != pat[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static bool ContainsBox(string path, string box) => BoxOffset(path, box) >= 0;
}
