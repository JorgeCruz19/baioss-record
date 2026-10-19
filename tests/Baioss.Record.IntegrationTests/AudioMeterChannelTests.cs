using System.Diagnostics;
using System.Globalization;
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
/// Cada medidor de audio enseña SU canal, con FFmpeg real y el motor de los canales, en preview y grabando: una fuente con un nivel
/// distinto en cada canal (el 1 en silencio; el canal k a −3·k dB: −6, −9… −48). Con el FFmpeg 8.0 estable de tools/, un ebur128 de
/// más de 2 canales escribe el pico del canal 1 en todos: con esta fuente, los 8 o 16 medidores en silencio (medido el 2026-10-19).
/// <list type="bullet">
///   <item>Se repite con otro FFmpeg si <c>BAIOSS_FFMPEG_ALT</c> apunta a él (carpeta o ffmpeg.exe): la build de junio de 2026 que trae
///   el instalador, que siempre midió bien.</item>
///   <item>Y con 16 canales sin SIMD (<c>-cpuflags 0</c>, como una CPU sin AVX): cada canal tiene su ebur128, así que no depende de la
///   versión de AVX que FFmpeg 8.0 usa con 2 canales. Una fuente estéreo sigue midiéndose con un ebur128 de 2 canales; sin AVX, el
///   8.0 da ahí el canal 1 en los dos medidores (los requisitos piden CPUs con AVX).</item>
/// </list>
/// </summary>
public sealed class AudioMeterChannelTests
{
    /// <summary>Otro FFmpeg con el que repetir la prueba (carpeta o ffmpeg.exe), además del de tools/.</summary>
    private const string AltFfmpegVariable = "BAIOSS_FFMPEG_ALT";

    /// <summary>Margen frente al nivel de cada canal: ebur128 escribe el true-peak con un decimal.</summary>
    private const double ToleranceDb = 0.5;

    private readonly ITestOutputHelper _out;
    public AudioMeterChannelTests(ITestOutputHelper output) => _out = output;

    [SkippableTheory]
    [InlineData("tools", 2, false)]
    [InlineData("tools", 8, false)]
    [InlineData("tools", 16, false)]
    [InlineData("tools", 16, true)]
    [InlineData("alt", 2, false)]
    [InlineData("alt", 8, false)]
    [InlineData("alt", 16, false)]
    public async Task EachMeter_ShowsItsOwnChannel_InPreviewAndWhileRecording(string ffmpeg, int channels, bool withoutSimd)
    {
        var locator = Locator(ffmpeg);
        _out.WriteLine($"FFmpeg: {locator.FfmpegPath} — {await VersionAsync(locator.FfmpegPath)}{(withoutSimd ? " · -cpuflags 0" : "")}");
        var expected = Enumerable.Range(1, channels).Select(ExpectedDb).ToArray();
        var source = new ChannelLevelsSource(channels, withoutSimd);
        await source.OpenAsync();
        var profile = new RecordingProfile
        {
            Name = "medidores", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(1), GopSize = 25,
            AudioCodec = AudioCodec.Pcm, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mkv,
        };
        var dir = Path.Combine(Path.GetTempPath(), $"baioss-meters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var meters = new MeterLog();
            await using (var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = dir })
            {
                engine.AudioPeaksUpdated += (_, peaks) => meters.Add(peaks);
                await engine.StartPreviewAsync(source, profile, "MTR");
                CheckEveryUpdate("preview", await meters.NextAsync(10), expected);

                await engine.StartRecordingAsync(Guid.NewGuid(), profile);
                CheckEveryUpdate("grabando", await meters.NextAsync(10), expected);
                await engine.StopRecordingAsync();
            }
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Nivel de cada canal (1-based) en la fuente y en su medidor: el 1 en silencio (el suelo), el canal k a −3·k dB.</summary>
    private static double ExpectedDb(int channel) => channel == 1 ? FfmpegMeterParser.FloorDb : -3.0 * channel;

    /// <summary>Todas las rondas de medidores de una fase: cuántos canales, y cada uno a su nivel.</summary>
    private void CheckEveryUpdate(string phase, IReadOnlyList<double[]> updates, double[] expected)
    {
        foreach (var u in updates.Take(2).Concat(updates.TakeLast(1)))
            _out.WriteLine($"{phase}: {string.Join(" ", u.Select(p => p.ToString("0.0", CultureInfo.InvariantCulture)))}");
        foreach (var peaks in updates)
        {
            Assert.Equal(expected.Length, peaks.Length);
            for (int c = 0; c < expected.Length; c++)
                Assert.True(Math.Abs(peaks[c] - expected[c]) <= ToleranceDb,
                    $"{phase}: el medidor {c + 1} marca {peaks[c]:0.0} dBFS y su canal lleva {expected[c]:0.0} " +
                    $"(todos: {string.Join(" ", peaks.Select(p => p.ToString("0.0", CultureInfo.InvariantCulture)))}).");
        }
    }

    private static FfmpegLocator Locator(string which)
    {
        if (which == "tools")
        {
            Skip.If(TestAssets.FfmpegDir is null, "FFmpeg no disponible en tools/.");
            return new FfmpegLocator(TestAssets.FfmpegDir!);
        }
        var alt = Environment.GetEnvironmentVariable(AltFfmpegVariable);
        Skip.If(string.IsNullOrWhiteSpace(alt), $"Sin otro FFmpeg con el que repetirla ({AltFfmpegVariable}).");
        return new FfmpegLocator(alt!);
    }

    private static async Task<string> VersionAsync(string ffmpeg)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-version");
        using var p = Process.Start(psi)!;
        string first = await p.StandardOutput.ReadLineAsync() ?? "";
        await p.WaitForExitAsync();
        return first;
    }

    /// <summary>Las rondas de medidores que publica el motor, en orden de llegada.</summary>
    private sealed class MeterLog
    {
        private readonly List<double[]> _updates = new();

        public void Add(IReadOnlyList<double> peaks) { lock (_updates) _updates.Add(peaks.ToArray()); }

        /// <summary>Las <paramref name="count"/> rondas que lleguen a partir de ahora (un segundo: una cada 100 ms).</summary>
        public async Task<IReadOnlyList<double[]>> NextAsync(int count)
        {
            int from;
            lock (_updates) from = _updates.Count;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (true)
            {
                lock (_updates)
                    if (_updates.Count >= from + count) return _updates.GetRange(from, count);
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Esperando {count} rondas de medidores (llegaron {_updates.Count - from}).");
                await Task.Delay(50);
            }
        }
    }

    /// <summary>
    /// Fuente en vivo (lavfi a tiempo real) con <c>channels</c> canales de audio, cada uno a su nivel (<see cref="ExpectedDb"/>): un tono
    /// de 997 Hz, que no cae en múltiplos de la frecuencia de muestreo y da el mismo pico en cualquier bloque de 100 ms.
    /// </summary>
    private sealed class ChannelLevelsSource(int channels, bool withoutSimd) : ICaptureSource
    {
        private static readonly SignalInfo Locked = new(SignalState.Locked, new Resolution(320, 180), FrameRate.P25, AudioLayout.Stereo,
            HasAudio: true, Timecode: null, Bitrate: null);

        public InputSource Definition { get; } = new() { Name = "niveles por canal", Type = InputType.File };
        public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
        public event EventHandler<SignalInfo>? SignalChanged;
        public int AudioChannelCount => channels;

        public Task OpenAsync(CancellationToken ct = default)
        {
            CurrentSignal = Locked with { AudioChannels = channels };
            SignalChanged?.Invoke(this, CurrentSignal);
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<string> BuildInputArguments()
        {
            var exprs = Enumerable.Range(1, channels).Select(c => c == 1
                ? "0"
                : string.Create(CultureInfo.InvariantCulture, $"{Math.Pow(10, ExpectedDb(c) / 20):0.000000}*sin(2*PI*997*t)"));
            var args = new List<string>();
            // Global de FFmpeg: vale aquí, delante de la entrada. Sin SIMD, como una CPU sin AVX.
            if (withoutSimd) args.AddRange(new[] { "-cpuflags", "0" });
            args.AddRange(new[]
            {
                "-re", "-f", "lavfi", "-i",
                $"testsrc2=size=320x180:rate=25[out0];aevalsrc=exprs={string.Join('|', exprs)}:s=48000:n=1920[out1]",
            });
            return args;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
