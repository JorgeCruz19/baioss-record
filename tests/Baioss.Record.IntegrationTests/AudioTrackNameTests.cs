using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Localization;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// El nombre de cada pista de audio («Canales 1-2», «Canales 3-4»…) llega al archivo TERMINADO, con FFmpeg real y el motor de
/// siempre: grabar, detener y ponerle nombre, que espera a la verificación y al remux a faststart. Con FFmpeg 8.0, en MP4/MOV
/// el <c>title</c> acaba en un átomo <c>udta/name</c> que ffprobe no lee y que ese remux pierde: ffprobe solo veía
/// «SoundHandler» en las ocho pistas de una DeckLink de 16 canales. Ahí el nombre va también como <c>handler_name</c>, que
/// ffprobe lee y el remux conserva. MKV guarda el <c>title</c>.
/// </summary>
public sealed class AudioTrackNameTests
{
    private readonly ITestOutputHelper _out;
    public AudioTrackNameTests(ITestOutputHelper output) => _out = output;

    [SkippableTheory]
    [InlineData(ContainerFormat.Mp4)]
    [InlineData(ContainerFormat.Mov)]
    [InlineData(ContainerFormat.Mkv)]
    public async Task EachPairTrack_KeepsItsName_InTheFinishedFile(ContainerFormat container)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = Path.Combine(Path.GetTempPath(), $"baioss-tracknames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Audio embebido de 8 canales con los cuatro pares elegidos: con el preset estéreo de fábrica (Single), una pista
            // estéreo por par. MP4/MOV con AAC y MKV con PCM, como sus presets.
            var source = new SwitchableSignalSource(SlateRealTimeTests.InputKind.Device, audioChannels: 8, pairs: "all");
            await source.OpenAsync();
            var profile = new RecordingProfile
            {
                Name = "pistas", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
                VideoBitrate = Bitrate.FromMbps(1), GopSize = 25, TargetResolution = new Resolution(640, 360),
                AudioCodec = container == ContainerFormat.Mkv ? AudioCodec.Pcm : AudioCodec.Aac,
                AudioLayout = AudioLayout.Stereo, Container = container,
            };

            string file;
            await using (var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = dir })
            {
                int frames = 0;
                engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);
                await engine.StartPreviewAsync(source, profile, "PST");
                await WaitUntilAsync(() => Volatile.Read(ref frames) >= 10, TimeSpan.FromSeconds(20), "preview en marcha");
                await engine.StartRecordingAsync(Guid.NewGuid(), profile);
                await Task.Delay(TimeSpan.FromSeconds(2));
                await engine.StopRecordingAsync();
                file = Assert.Single(await engine.RenameSessionFilesAsync("Pistas")).New;
            }

            var tracks = await AudioTrackTagsAsync(locator.FfprobePath, file);
            foreach (var (title, handler) in tracks) _out.WriteLine($"{Path.GetFileName(file)}: title={title ?? "-"} · handler_name={handler ?? "-"}");
            var expected = new[] { "1-2", "3-4", "5-6", "7-8" }.Select(pair => Localizer.F("Audio_TrackTitle", pair)).ToArray();
            if (container == ContainerFormat.Mkv)
            {
                Assert.Equal(expected, tracks.Select(t => t.Title));
                return;
            }
            // Sin fragmentos: el remux a faststart ya pasó, y el nombre sobrevivió a él.
            Assert.False(ContainsAscii(file, "moof"), "El archivo debía quedar optimizado (faststart) al detener.");
            Assert.Equal(expected, tracks.Select(t => t.HandlerName));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Etiquetas <c>title</c> y <c>handler_name</c> de cada pista de audio, en orden, tal como las ve ffprobe.</summary>
    private static async Task<IReadOnlyList<(string? Title, string? HandlerName)>> AudioTrackTagsAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_tags=title,handler_name", "-of", "json", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"ffprobe falló con {Path.GetFileName(file)}: {await stderr}");
        using var json = JsonDocument.Parse(await stdout);
        var tracks = new List<(string?, string?)>();
        foreach (var s in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            var tags = s.TryGetProperty("tags", out var t) ? t : default;
            tracks.Add((Tag(tags, "title"), Tag(tags, "handler_name")));
        }
        return tracks;

        static string? Tag(JsonElement tags, string name) =>
            tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out var v) ? v.GetString() : null;
    }

    private static bool ContainsAscii(string path, string token)
    {
        var bytes = File.ReadAllBytes(path);
        var pattern = Encoding.ASCII.GetBytes(token);
        return bytes.AsSpan().IndexOf(pattern) >= 0;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Esperando: " + what);
            await Task.Delay(50);
        }
    }
}
