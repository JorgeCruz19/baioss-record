using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Sincronía de audio y vídeo en los contenedores que no guardan la reordenación del códec (AVI; MXF con H.264), por el camino
/// de la aplicación (el proceso del canal, <see cref="FfmpegChannelEngine"/>, con el MP4 estándar y la cola de escritura de la
/// build publicada): una fuente con un destello blanco y un pitido de 100 ms al empezar cada segundo se graba con el perfil y
/// con el mismo perfil en MP4, y el pitido tiene que quedar respecto a su destello igual en los dos, con medio cuadro de margen.
/// Antes, en AVI el vídeo quedaba 2 cuadros (80 ms a 25 fps) por detrás del audio con los B-frames de x264 (también en MXF), y
/// uno con QuickSync, con NVENC (su SEI de temporización) y con MPEG-2 (sin low_delay). Un códec por hardware que no abre en
/// este equipo se omite.
/// </summary>
public sealed class ContainerAvSyncTests
{
    private readonly ITestOutputHelper _out;
    public ContainerAvSyncTests(ITestOutputHelper output) => _out = output;

    /// <summary>Lo que se graba de cada perfil: las marcas de los primeros segundos.</summary>
    private static readonly TimeSpan RecordFor = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Fuente en vivo sintética con las marcas: lavfi a tiempo real, 1080p25, el primer cuadro de cada segundo en blanco y un tono
    /// de 1 kHz a −6 dBFS sus primeros 100 ms (a la muestra: aevalsrc). Destello y pitido empiezan a la vez en la fuente.
    /// </summary>
    private sealed class MarkedSource : ICaptureSource
    {
        public InputSource Definition { get; } = new() { Name = "marcas", Type = InputType.File, Uri = "lavfi" };
        public SignalInfo CurrentSignal { get; } = new(SignalState.Locked, Resolution.Hd1080, FrameRate.P25,
            AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null);
        public event EventHandler<SignalInfo>? SignalChanged { add { } remove { } }
        public bool DeliversInRealTime => true;

        public IReadOnlyList<string> BuildInputArguments() => new[]
        {
            "-re", "-f", "lavfi", "-i",
            "color=c=black:s=1920x1080:r=25,drawbox=c=white:t=fill:enable='eq(mod(n,25),0)'[out0];" +
            "aevalsrc='0.5*sin(2*PI*1000*t)*lt(mod(t,1),0.1)':s=48000:c=stereo[out1]",
        };

        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Los dos presets AVI de fábrica tal cual (x264, progresivo y entrelazado), el progresivo con los demás códecs que se pueden
    /// elegir en AVI y que reordenaban o retenían un cuadro (QuickSync, NVENC, MPEG-2) y en MXF, que solo guarda la reordenación
    /// de MPEG-2.
    /// </summary>
    [SkippableTheory]
    [InlineData("AVI · H.264 1080p25", VideoCodec.H264x264, ContainerFormat.Avi)]
    [InlineData("AVI · H.264 1080i25", VideoCodec.H264x264, ContainerFormat.Avi)]
    [InlineData("AVI · H.264 1080p25", VideoCodec.H264Qsv, ContainerFormat.Avi)]
    [InlineData("AVI · H.264 1080p25", VideoCodec.H264Nvenc, ContainerFormat.Avi)]
    [InlineData("AVI · H.264 1080p25", VideoCodec.Mpeg2Video, ContainerFormat.Avi)]
    [InlineData("AVI · H.264 1080p25", VideoCodec.H264x264, ContainerFormat.Mxf)]
    public async Task TheBeep_StaysWithItsFlash_AsInMp4(string presetName, VideoCodec codec, ContainerFormat container)
    {
        Skip.If(TestAssets.FfmpegDir is null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var profile = PresetCatalog.CreateBuiltIns().Single(p => p.Name == presetName).ToProfile();
        // Como el operador que cambia el códec de una copia del preset; los de GPU, con su formato de píxel nativo y solo si abren.
        profile.VideoCodec = codec;
        if (FfmpegCodecMap.IsGpuEncoder(codec) || codec is VideoCodec.H264Qsv or VideoCodec.HevcQsv or VideoCodec.H264Amf)
        {
            profile.PixelFormat = PixelFormat.Auto;
            string encoder = FfmpegCodecMap.VideoEncoder(codec);
            Skip.IfNot(await locator.IsVideoEncoderUsableAsync(encoder), $"{encoder} no abre en este equipo.");
        }
        profile.Container = container;
        var reference = profile.Clone();
        reference.Container = ContainerFormat.Mp4;

        var outputRoot = Path.Combine(Path.GetTempPath(), $"baioss-avsync-{Guid.NewGuid():N}");
        try
        {
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance)
            {
                OutputRoot = outputRoot,
                FragmentedMp4 = false,
                WriteQueue = await locator.SupportsOutputThreadQueueAsync() ? TimeSpan.FromSeconds(10) : TimeSpan.Zero,
            };
            bool fellBack = false;
            engine.AlarmChanged += (_, a) => { if (a.Type == AlarmType.EncoderFallback && a.Active) fellBack = true; };
            int frames = 0;
            engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);
            await engine.StartPreviewAsync(new MarkedSource(), profile, "AVS");
            await WaitForAsync(() => Volatile.Read(ref frames) >= 3, TimeSpan.FromSeconds(20));

            var offsets = await RecordAndMeasureAsync(engine, locator, profile);
            var expected = await RecordAndMeasureAsync(engine, locator, reference);
            Assert.False(fellBack, $"{FfmpegCodecMap.VideoEncoder(codec)} no abrió y el canal grabó con otro codificador.");

            double frame = 1 / (profile.OutputFrameRate?.Value ?? 25);
            Assert.True(expected.Count >= 3 && offsets.Count >= 3, $"Marcas medidas: {offsets.Count} en {container}, {expected.Count} en MP4.");
            Assert.InRange(expected.Max() - expected.Min(), 0, 0.011); // la fuente es exacta: el desfase no cambia de una marca a otra
            double shift = offsets.Average() - expected.Average();
            Assert.True(Math.Abs(shift) <= frame / 2,
                $"En {container} el pitido queda {shift * 1000:+0;-0} ms respecto a su destello más que en MP4 " +
                $"({shift / frame:+0.0;-0.0} cuadros): el vídeo se presenta {(shift < 0 ? "tarde" : "antes de tiempo")}.");
        }
        finally
        {
            try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Graba <see cref="RecordFor"/> con el perfil y devuelve, por cada destello, cuánto va su pitido detrás (s).</summary>
    private async Task<List<double>> RecordAndMeasureAsync(FfmpegChannelEngine engine, FfmpegLocator locator, RecordingProfile profile)
    {
        await engine.StartRecordingAsync(Guid.NewGuid(), profile);
        await Task.Delay(RecordFor);
        await engine.StopRecordingAsync();
        var file = engine.LastOutputFile;
        Assert.True(file is not null && File.Exists(file), $"No se generó el archivo: {file}");

        var (video, _, _) = await RunAsync(locator.FfprobePath, "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,has_b_frames", "-of", "compact=p=0", file!);
        var (flashes, beeps) = await SyncMarksAsync(locator.FfmpegPath, file!);
        var offsets = flashes.Select(f => beeps.Select(b => b - f).Where(o => Math.Abs(o) < 0.5).DefaultIfEmpty(double.NaN).First())
                             .Where(o => !double.IsNaN(o)).ToList();
        string I(double d) => d.ToString("0.000", CultureInfo.InvariantCulture);
        _out.WriteLine($"{Path.GetFileName(file)} ({video.Trim()}): destellos {string.Join(" ", flashes.Select(I))}");
        _out.WriteLine($"  pitido − destello: {string.Join(" ", offsets.Select(o => o.ToString("+0.000;-0.000", CultureInfo.InvariantCulture)))}");
        return offsets;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout) await Task.Delay(100);
    }

    private static async Task<(string Out, string Err, int Exit)> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await o, await e, p.ExitCode);
    }

    /// <summary>
    /// Instantes (s) de las marcas de un archivo, como los presenta FFmpeg al decodificarlo: el primer cuadro de cada destello
    /// (brillo medio alto tras uno oscuro) y el arranque de cada pitido (pico de un bloque de 5 ms por encima de −30 dBFS tras uno
    /// en silencio). Una sola pasada, para que vídeo y audio compartan el origen de tiempos. Una marca que ya estaba en marcha al
    /// empezar el archivo no se puede fechar: solo cuentan las que empiezan tras un cuadro oscuro o un bloque en silencio.
    /// </summary>
    private static async Task<(List<double> Flashes, List<double> Beeps)> SyncMarksAsync(string ffmpeg, string file)
    {
        var (_, err, _) = await RunAsync(ffmpeg, "-hide_banner", "-nostats", "-i", file, "-filter_complex",
            "[0:v:0]scale=64:36,format=gray,showinfo[v];" +
            "[0:a:0]asetnsamples=n=240:p=0,astats=metadata=1:reset=1,ametadata=mode=print:key=lavfi.astats.Overall.Peak_level[a]",
            "-map", "[v]", "-f", "null", "-", "-map", "[a]", "-f", "null", "-");
        var flashes = new List<double>();
        var beeps = new List<double>();
        bool bright = true, loud = true;
        double audioAt = double.NaN;
        static double After(string line, string key, char end)
        {
            int i = line.IndexOf(key, StringComparison.Ordinal) + key.Length;
            int j = line.IndexOf(end, i);
            var text = (j < 0 ? line[i..] : line[i..j]).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }
        foreach (var line in err.Split('\n'))
        {
            if (line.Contains("Parsed_showinfo", StringComparison.Ordinal) && line.Contains(" mean:[", StringComparison.Ordinal))
            {
                double at = After(line, "pts_time:", ' '), mean = After(line, " mean:[", ']');
                bool isBright = mean > 140;
                if (isBright && !bright) flashes.Add(at);
                bright = isBright;
            }
            else if (line.Contains("Parsed_ametadata", StringComparison.Ordinal) && line.Contains("pts_time:", StringComparison.Ordinal))
                audioAt = After(line, "pts_time:", ' ');
            else if (line.Contains("lavfi.astats.Overall.Peak_level=", StringComparison.Ordinal))
            {
                var text = line[(line.IndexOf('=') + 1)..].Trim();
                double peak = text.StartsWith("-inf", StringComparison.Ordinal) ? double.NegativeInfinity
                    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : double.NegativeInfinity;
                if (peak > -30 && !loud) beeps.Add(audioAt);
                if (peak > -30) loud = true;
                else if (peak < -40) loud = false;
            }
        }
        return (flashes, beeps);
    }
}
