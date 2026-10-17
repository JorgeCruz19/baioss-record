using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Preview de una fuente ENTRELAZADA con el motor real: una barra blanca vertical que cruza la imagen deprisa en un 1080i29.97
/// TFF sintético, como lo que entrega una DeckLink con una señal 1080i59.94. Reducir a 640×360 el cuadro con los dos campos
/// entretejidos enseñaba la barra DOS veces, a medio brillo (una por campo): la doble imagen que se veía en todo lo que se
/// mueve. Con el preview desentrelazado cada frame la enseña UNA vez y entera, al ritmo elegido (un cuadro por campo o por
/// cuadro), y la telemetría sigue contando frames de la fuente.
/// </summary>
public sealed class InterlacedPreviewTests
{
    private readonly ITestOutputHelper _out;
    public InterlacedPreviewTests(ITestOutputHelper output) => _out = output;

    /// <summary>La escena, en sintaxis de lavfi: una barra de 24 px que va y viene (un seno de 2 s, hasta ~2800 px/s: 47 px
    /// entre un campo y el siguiente, más que su anchura) sobre gris oscuro, entrelazada TFF y en uyvy422 como la tarjeta. En
    /// 4:2:2 desde el principio: entrelazar cuadros 4:2:0 mezclaría en cada campo el croma del otro.</summary>
    private const string MovingBar1080i =
        "color=c=0x202020:s=1920x1080:r=60000/1001[bg];color=c=white:s=24x1080:r=60000/1001[bar];" +
        "[bg][bar]overlay=x='948+900*sin(PI*t)':y=0:shortest=1:format=yuv422,interlace=scan=tff:lowpass=off,format=uyvy422";

    /// <summary>Fuente en vivo con esa escena (a tiempo real) que se declara entrelazada TFF, como el relé de una DeckLink.</summary>
    private sealed class MovingBarSource : ICaptureSource
    {
        public InputSource Definition { get; } = new() { Name = "barra 1080i", Type = InputType.File, Uri = "lavfi" };
        public SignalInfo CurrentSignal { get; } = new(SignalState.Locked, new Resolution(1920, 1080), FrameRate.P2997,
            AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null, FormatLabel: "1920×1080 · 59.94i", Scan: ScanType.InterlacedTff);
        public event EventHandler<SignalInfo>? SignalChanged { add { } remove { } }
        public bool DeliversInRealTime => true;
        public IReadOnlyList<string> BuildInputArguments() => new[]
        {
            "-re", "-f", "lavfi", "-i", $"{MovingBar1080i}[out0];sine=frequency=1000:sample_rate=48000[out1]",
        };
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static RecordingProfile Profile() => new()
    {
        Name = "il", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(4), GopSize = 30,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    /// <summary>Brillo máximo y barras (tramos de más de 100 de luma) en una fila de un frame BGRA.</summary>
    internal static (int Max, int Bars) RowProfile(byte[] bgra, int width, int stride, int y)
    {
        int max = 0, bars = 0;
        bool inBar = false;
        for (int x = 0; x < width; x++)
        {
            int i = y * stride + x * 4;
            int luma = (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8; // B, G, R
            max = Math.Max(max, luma);
            bool bright = luma > 100;
            if (bright && !inBar) bars++;
            inBar = bright;
        }
        return (max, bars);
    }

    /// <summary>Una sola barra a pleno brillo (la barra es blanca: 255; cada fantasma de la cadena de antes, la mitad).</summary>
    internal static bool Clean((int Max, int Bars) p) => p.Max >= 200 && p.Bars == 1;

    [SkippableTheory]
    [InlineData(true, 2)]  // un cuadro por campo: 59.94 desde 1080i59.94
    [InlineData(false, 1)] // el primer campo de cada cuadro: 29.97
    public async Task AnInterlacedSource_IsPreviewedWithoutGhosting_AtTheChosenCadence_AndTheTelemetryCountsSourceFrames(bool fieldRate, int perSourceFrame)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-interlaced-{Guid.NewGuid():N}");
        var clock = Stopwatch.StartNew();
        var frames = new List<(double At, int Max, int Bars)>();
        try
        {
            await using var engine = new FfmpegChannelEngine(new FfmpegLocator(TestAssets.FfmpegDir!), NullLogger<FfmpegChannelEngine>.Instance)
            {
                OutputRoot = outputRoot, FieldRatePreview = fieldRate,
            };
            // El búfer del frame se reutiliza al volver: se mide aquí mismo, en la fila central.
            engine.FrameReady += (_, f) =>
            {
                var p = RowProfile(f.Bgra, f.Width, f.Stride, f.Height / 2);
                lock (frames) frames.Add((clock.Elapsed.TotalSeconds, p.Max, p.Bars));
            };
            await engine.StartPreviewAsync(new MovingBarSource(), Profile(), "IL");
            await RecordingRecoveryWakeTests.WaitUntilAsync(() => { lock (frames) return frames.Count > 60; }, TimeSpan.FromSeconds(20), "preview en marcha");

            await Task.Delay(1000);
            double t0 = clock.Elapsed.TotalSeconds;
            await Task.Delay(5000);
            double t1 = clock.Elapsed.TotalSeconds;
            List<(double At, int Max, int Bars)> window;
            lock (frames) window = frames.Where(f => f.At >= t0 && f.At < t1).ToList();
            double previewFps = window.Count / (t1 - t0);
            double sourceFps = engine.Stats.OutputFps;
            int clean = window.Count(f => Clean((f.Max, f.Bars)));
            _out.WriteLine($"{(fieldRate ? "por campo" : "un campo")}: preview {previewFps:0.00} fps, FPS OUT {sourceFps:0.00}, " +
                           $"frames {engine.Stats.FrameCount}, limpios {clean} de {window.Count}, brillo mínimo de la barra {window.Min(f => f.Max)}, " +
                           $"barras {string.Join(",", window.Select(f => f.Bars).Distinct().Order())}");

            // Cadencia: el preview llega a la de la fuente (o a la de sus campos); FPS OUT sigue en frames de la fuente. (Un FFmpeg
            // 8.0 lee una entrada con -re a 1,05× los primeros ~10 s: de ahí los márgenes; la proporción entre los dos no cambia.)
            Assert.InRange(sourceFps, 26, 34);
            Assert.InRange(previewFps / sourceFps, perSourceFrame * 0.85, perSourceFrame * 1.15);
            // Sin fantasma: en todos los frames, una sola barra y entera.
            Assert.Equal(window.Count, clean);
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task ReducingBothFieldsTogether_ShowedTheMovingBarTwice_AtHalfBrightness()
    {
        // El control de la prueba de arriba: la cadena de antes (reducir el cuadro entretejido de una vez) sobre la misma escena.
        // Mientras la barra va deprisa (dos tercios del tiempo), cada frame la enseña dos veces a medio brillo.
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        const int width = 640, height = 360, count = 120;
        var psi = new ProcessStartInfo(new FfmpegLocator(TestAssets.FfmpegDir!).FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-f", "lavfi", "-i", MovingBar1080i,
                     "-frames:v", count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-vf", $"scale={width}:{height},format=bgra", "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var ffmpeg = Process.Start(psi)!;
        var stderr = ffmpeg.StandardError.ReadToEndAsync();
        var frame = new byte[width * height * 4];
        var profiles = new List<(int Max, int Bars)>();
        var stdout = ffmpeg.StandardOutput.BaseStream;
        while (true)
        {
            int read = 0;
            while (read < frame.Length)
            {
                int n = await stdout.ReadAsync(frame.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
            if (read < frame.Length) break;
            profiles.Add(RowProfile(frame, width, width * 4, height / 2));
        }
        await ffmpeg.WaitForExitAsync();
        _out.WriteLine($"cadena de antes: {profiles.Count(p => !Clean(p))} de {profiles.Count} frames con la barra doble o a medio brillo; " +
                       $"brillo mínimo {profiles.Min(p => p.Max)}");
        Assert.True(profiles.Count == count, $"FFmpeg dio {profiles.Count} frames: {await stderr}");
        Assert.True(profiles.Count(p => !Clean(p)) > count / 3, "La cadena de antes debería enseñar la barra doble mientras va deprisa.");
    }
}
