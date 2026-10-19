using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Cada preset de fábrica graba lo que dice, y en tiempo real: con el FFmpeg real y el canal completo (el pre-vuelo del validador y
/// el motor unificado, con la cola de escritura y el fMP4 de la aplicación), <see cref="RecordSeconds"/> s de una fuente en vivo, y
/// lo grabado releído entero (<see cref="RecordedFileCheck"/>):
/// <list type="bullet">
///   <item>el códec, el perfil (ProRes, DNxHD/HR), el formato de píxel, el tamaño, la cadencia, el barrido y el audio del preset;</item>
///   <item>en tiempo real: el proceso va al día, sin la alarma de frames perdidos y sin degradar el codificador;</item>
///   <item>legible: se decodifica entero sin errores, con los cuadros que tocan y el audio tan largo como el vídeo.</item>
/// </list>
/// Tres fuentes: la del formato del propio preset (1080p25 si graba el nativo), la de las DeckLink de este equipo (1080i59.94 TFF en
/// uyvy422, que los presets progresivos desentrelazan y los demás escalan o recadencian) y, para los presets entrelazados, un 1080p
/// al doble de su cadencia (1080p59.94 de un emisor NDI para un 29.97i, 1080p50 para un 25i), que tejen. Unos 20 s por fila: solo
/// corre con <c>BAIOSS_PRESET_SWEEP=1</c>.
/// </summary>
public sealed class FactoryPresetRecordingTests
{
    private const double RecordSeconds = 10;
    private static readonly object ReportLock = new();
    private readonly ITestOutputHelper _output;
    public FactoryPresetRecordingTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string> FactoryPresets()
    {
        var data = new TheoryData<string>();
        foreach (var p in PresetCatalog.CreateBuiltIns()) data.Add(p.Name);
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(FactoryPresets))]
    public Task FromItsOwnFormat_RecordsWhatItSays_InRealTime(string preset) => RecordAsync(preset, Feed.Own);

    [SkippableTheory]
    [MemberData(nameof(FactoryPresets))]
    public Task FromA1080i5994Feed_RecordsWhatItSays_InRealTime(string preset) => RecordAsync(preset, Feed.DeckLink);

    [SkippableTheory]
    [MemberData(nameof(FactoryPresets))]
    public Task FromAProgressiveFeedAtTwiceItsRate_RecordsWhatItSays_InRealTime(string preset) => RecordAsync(preset, Feed.ProgressiveDouble);

    public enum Feed { Own, DeckLink, ProgressiveDouble }

    private async Task RecordAsync(string name, Feed kind)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("BAIOSS_PRESET_SWEEP") == "1",
            "Barrido de los presets de fábrica (~20 s por fila): BAIOSS_PRESET_SWEEP=1 para correrlo.");
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        // Para repetir unas filas: BAIOSS_PRESET_ONLY con trozos de sus nombres («NVENC», «1080i59.94|ProRes»), y BAIOSS_PRESET_FEEDS
        // con las fuentes («DeckLink,Own»).
        if (Environment.GetEnvironmentVariable("BAIOSS_PRESET_ONLY") is { Length: > 0 } only)
            Skip.IfNot(only.Split('|').Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase)), $"Fuera de BAIOSS_PRESET_ONLY={only}.");
        if (Environment.GetEnvironmentVariable("BAIOSS_PRESET_FEEDS") is { Length: > 0 } feeds)
            Skip.IfNot(feeds.Split(',').Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase), $"Fuera de BAIOSS_PRESET_FEEDS={feeds}.");
        var preset = PresetCatalog.CreateBuiltIns().Single(p => p.Name == name);
        // El 1080p al doble de cadencia es para ver cómo tejen los campos los presets entrelazados; uno progresivo solo recadenciaría.
        Skip.If(kind is Feed.ProgressiveDouble && preset.ScanType is ScanType.Progressive, "La fuente progresiva al doble es para los presets entrelazados.");
        var profile = preset.ToProfile();
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);

        var feed = kind switch
        {
            Feed.DeckLink => new LiveFeed(1920, 1080, FrameRate.P2997, ScanType.InterlacedTff, "uyvy422"),
            Feed.ProgressiveDouble => new LiveFeed(1920, 1080, new FrameRate(preset.FrameRateNum * 2, preset.FrameRateDen), ScanType.Progressive, "uyvy422"),
            _ => new LiveFeed(preset.Width ?? 1920, preset.Height ?? 1080,
                preset.FrameRateNum > 0 ? new FrameRate(preset.FrameRateNum, preset.FrameRateDen) : FrameRate.P25, preset.ScanType),
        };
        var dir = Path.Combine(Path.GetTempPath(), $"baioss-fabrica-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); }
        var started = DateTime.Now;
        try
        {
            await feed.OpenAsync();
            var queue = await locator.SupportsOutputThreadQueueAsync() ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
            await using var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger("Capture")) { OutputRoot = dir, WriteQueue = queue };
            var alarms = new List<string>();
            capture.AlarmChanged += (_, a) => { if (a.Active) lock (alarms) alarms.Add(a.Type.ToString()); };
            var unverified = new List<string>();
            capture.FileUnverified += (_, f) => { lock (unverified) unverified.Add($"{Path.GetFileName(f.FilePath)} ({f.SizeBytes} B)"); };
            int previews = 0;
            capture.FrameReady += (_, _) => Interlocked.Increment(ref previews);
            await capture.StartPreviewAsync(feed, profile, "FAB");
            await RecordingRecoveryWakeTests.WaitUntilAsync(() => Volatile.Read(ref previews) > 5, TimeSpan.FromSeconds(20), "preview de la fuente");
            await using var channel = new StandaloneChannelEngine("FAB", feed, profile, capture, log: new Logger<StandaloneChannelEngine>(loggers));

            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            var clock = Stopwatch.StartNew();
            var samples = new List<(double At, long MediaUs, double Cpu)>();
            var states = new HashSet<RecordingState>();
            var channelAlarms = new HashSet<string>();
            while (clock.Elapsed.TotalSeconds < RecordSeconds)
            {
                await Task.Delay(500);
                samples.Add((clock.Elapsed.TotalSeconds, capture.Stats.MediaTimeUs, RecordedFileCheck.FfmpegCpuSeconds(started)));
                states.Add(capture.State);
                foreach (var a in channel.Status.Alarms ?? Array.Empty<ChannelAlarm>()) channelAlarms.Add(a.Type.ToString());
            }
            double recorded = clock.Elapsed.TotalSeconds;
            await channel.StopRecordingAsync(RecordingStopReason.Operator);
            await RecordedFileCheck.WaitForTheEngineAsync(logs, capture.LastOutputFile, profile.Container);

            // Tiempo real: medios procesados frente al reloj, y núcleos que se lleva el proceso del canal (preview incluido), del
            // segundo 3 al final.
            var steady = samples.Where(s => s.At >= 3 && s.MediaUs >= 0).ToList();
            double speed = steady.Count >= 2 ? (steady[^1].MediaUs - steady[0].MediaUs) / 1e6 / (steady[^1].At - steady[0].At) : double.NaN;
            double cores = steady.Count >= 2 ? (steady[^1].Cpu - steady[0].Cpu) / (steady[^1].At - steady[0].At) : double.NaN;
            string encoder;
            lock (logs) encoder = logs.Where(l => l.Contains("grabación", StringComparison.Ordinal) && l.Contains("-c:v ", StringComparison.Ordinal))
                                      .Select(l => Regex.Match(l, @"-c:v (\S+)").Groups[1].Value).LastOrDefault() ?? "—";
            if (profile.AudioOnly) encoder = "—";

            var file = capture.LastOutputFile;
            Expect(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, $"no hay archivo grabado ({file ?? "ninguno"})");
            Expect(states.SetEquals(new[] { RecordingState.Recording }), $"estados mientras grababa: {string.Join(", ", states)}");
            lock (alarms) Expect(!alarms.Any(a => a is nameof(AlarmType.EncoderFallback) or nameof(AlarmType.RecordingFailed)),
                $"alarmas del motor: {string.Join(", ", alarms)}");
            Expect(!channelAlarms.Contains(nameof(AlarmType.FramesDropped)), "alarma de frames perdidos mientras grababa");
            lock (unverified) Expect(unverified.Count == 0, $"archivos sin verificar: {string.Join(", ", unverified)}");
            Expect(!(speed < 0.97), $"el proceso va al {speed * 100:0} % del tiempo real");

            string summary = $"{speed:0.00}× · {cores:0.0} núcleos · {encoder}";
            if (file is not null && File.Exists(file) && new FileInfo(file).Length > 0)
            {
                var expected = new RecordedFileCheck.Expected(profile, profile.TargetResolution ?? feed.Size, profile.OutputFrameRate ?? feed.Rate,
                    preset.AudioChannels, recorded) { VideoProfile = DnxProfileName(profile), SourceScan = feed.Scan };
                var (contents, wrong) = await RecordedFileCheck.CheckAsync(locator, file, expected);
                summary += " · " + contents;
                problems.AddRange(wrong);
            }
            _output.WriteLine($"{name} ← {feed.Label}: {summary}");
            foreach (var p in problems) _output.WriteLine("  ✗ " + p);
            // Una línea por fila en BAIOSS_PRESET_REPORT, para leer el barrido entero de una vez.
            if (Environment.GetEnvironmentVariable("BAIOSS_PRESET_REPORT") is { Length: > 0 } report)
                lock (ReportLock)
                    File.AppendAllText(report, $"{(problems.Count == 0 ? "OK" : "FALLA")}\t{name}\t{feed.Label}\t{summary}\t{string.Join(" | ", problems)}{Environment.NewLine}");
            if (problems.Count > 0)
                lock (logs)
                    foreach (var l in logs.Where(l => l.StartsWith("[Warning]", StringComparison.Ordinal) || l.StartsWith("[Error]", StringComparison.Ordinal)
                                                      || l.Contains("Pipeline canal", StringComparison.Ordinal)).Take(12))
                        _output.WriteLine("  " + l);
            if (Environment.GetEnvironmentVariable("BAIOSS_PRESET_KEEP") == "1") _output.WriteLine($"  archivo: {file}");
        }
        finally
        {
            await feed.DisposeAsync();
            TryDelete(dir);
        }
        Assert.True(problems.Count == 0, $"{name} ← {feed.Label}:\n" + string.Join("\n", problems));
    }

    /// <summary>El perfil que ffprobe nombra en los DNx de fábrica: los DNxHD son los 1080i (CID clásico); los DNxHR, por perfil.</summary>
    private static string? DnxProfileName(RecordingProfile p) => p.VideoCodec switch
    {
        VideoCodec.DnxHd => "DNXHD",
        VideoCodec.DnxHr when p.ScanType is ScanType.Progressive => p.EncoderProfile switch
        {
            EncoderProfile.DnxHrLb => "DNXHR LB", EncoderProfile.DnxHrSq => "DNXHR SQ", EncoderProfile.DnxHrHqx => "DNXHR HQX",
            EncoderProfile.DnxHr444 => "DNXHR 444", _ => "DNXHR HQ",
        },
        _ => null,
    };

    private static void TryDelete(string dir)
    {
        if (Environment.GetEnvironmentVariable("BAIOSS_PRESET_KEEP") == "1") return;
        for (int i = 0; i < 20; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch { Thread.Sleep(300); } // el remux de MOV/MP4 o la verificación pueden tener aún el archivo abierto
        }
    }
}
