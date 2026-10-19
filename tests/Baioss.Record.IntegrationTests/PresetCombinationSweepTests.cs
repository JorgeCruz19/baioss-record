using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Recording;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Las combinaciones que puede pedir un preset propio (o importado), frente al validador y con el FFmpeg real: cada una se graba
/// <see cref="RecordSeconds"/> s con el MOTOR solo —sin el pre-vuelo, como lo grabaría un canal si el validador la dejara pasar— y
/// lo grabado se relee entero (<see cref="RecordedFileCheck"/>). La regla: lo que el validador acepta graba lo que pide (o, con un
/// codificador por hardware que el equipo no tiene o que no hace lo pedido, degrada con su alarma al siguiente de su cadena, que lo
/// graba por CPU). Lo que rechaza solo se anota,
/// con lo que habría hecho FFmpeg. Un eje cada vez a partir de un perfil que graba: formato de píxel, barrido, perfil intra, control
/// de tasa, tamaño, cadencia, distribución y bitrate del audio, solo audio, GOP y bitrate de vídeo. ~8 s por fila: solo corre con
/// <c>BAIOSS_COMBO_SWEEP=1</c> (y <c>BAIOSS_COMBO_ONLY</c> con trozos de los nombres para repetir unas filas).
/// </summary>
[Collection("Localizer")]
public sealed class PresetCombinationSweepTests
{
    private const double RecordSeconds = 3;
    private static readonly object ReportLock = new();
    private readonly ITestOutputHelper _output;
    public PresetCombinationSweepTests(ITestOutputHelper output) => _output = output;

    /// <summary>Una combinación: su nombre (único: es la fila de la teoría), el perfil y la fuente.</summary>
    private sealed record Case(string Name, Func<RecordingProfile> Profile, Func<LiveFeed> Feed);

    private static readonly Lazy<Dictionary<string, Case>> All = new(() => BuildCases().ToDictionary(c => c.Name));

    public static TheoryData<string> Combinations()
    {
        var data = new TheoryData<string>();
        foreach (var name in All.Value.Keys) data.Add(name);
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(Combinations))]
    public async Task WhatTheValidatorAccepts_RecordsWhatItAsks(string name)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("BAIOSS_COMBO_SWEEP") == "1",
            "Barrido de combinaciones de un preset propio (~8 s por fila): BAIOSS_COMBO_SWEEP=1 para correrlo.");
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        if (Environment.GetEnvironmentVariable("BAIOSS_COMBO_ONLY") is { Length: > 0 } only)
            Skip.IfNot(only.Split('|').Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase)), $"Fuera de BAIOSS_COMBO_ONLY={only}.");
        var language = Localizer.Language;
        Localizer.Language = AppLanguage.Spanish;
        try { await RunAsync(All.Value[name]); }
        finally { Localizer.Language = language; }
    }

    private async Task RunAsync(Case c)
    {
        var profile = c.Profile();
        var verdict = RecordingProfileValidator.Validate(profile);
        bool accepted = verdict.Count == 0;
        // Solo el veredicto del validador, sin grabar (BAIOSS_COMBO_VERDICTS): para cruzarlo con un barrido ya hecho.
        if (Environment.GetEnvironmentVariable("BAIOSS_COMBO_VERDICTS") is { Length: > 0 } verdicts)
        {
            lock (ReportLock) File.AppendAllText(verdicts, $"{c.Name}\t{(accepted ? "acepta" : "rechaza")}\t{string.Join(" ", verdict)}{Environment.NewLine}");
            return;
        }
        // Lo que el validador rechaza no llega a grabarse en un canal: con BAIOSS_COMBO_ACCEPTED_ONLY=1 no se graba (la regla de la
        // prueba es sobre lo aceptado; lo rechazado solo documenta qué haría FFmpeg).
        Skip.If(!accepted && Environment.GetEnvironmentVariable("BAIOSS_COMBO_ACCEPTED_ONLY") == "1", $"El validador la rechaza: {string.Join(" ", verdict)}");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var feed = c.Feed();
        var dir = Path.Combine(Path.GetTempPath(), $"baioss-combo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        string outcome;
        string summary = "";
        var problems = new List<string>();
        try
        {
            await feed.OpenAsync();
            await using var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger("Capture")) { OutputRoot = dir };
            var alarms = new List<string>();
            capture.AlarmChanged += (_, a) => { if (a.Active) lock (alarms) alarms.Add(a.Type.ToString()); };
            int previews = 0;
            capture.FrameReady += (_, _) => Interlocked.Increment(ref previews);
            await capture.StartPreviewAsync(feed, profile, "CMB");
            await RecordingRecoveryWakeTests.WaitUntilAsync(() => Volatile.Read(ref previews) > 3, TimeSpan.FromSeconds(30), "preview de la fuente");

            Exception? startError = null;
            try { await capture.StartRecordingAsync(Guid.NewGuid(), profile, "combo"); }
            catch (Exception ex) { startError = ex; }
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < RecordSeconds) await Task.Delay(250);
            double recorded = clock.Elapsed.TotalSeconds;
            if (startError is null && capture.State is RecordingState.Recording or RecordingState.Starting) await capture.StopRecordingAsync();
            await RecordedFileCheck.WaitForTheEngineAsync(logs, capture.LastOutputFile, profile.Container);

            string encoder;
            lock (logs) encoder = logs.Where(l => l.Contains("grabación", StringComparison.Ordinal) && l.Contains("-c:v ", StringComparison.Ordinal))
                                      .Select(l => Regex.Match(l, @"-c:v (\S+)").Groups[1].Value).LastOrDefault() ?? "—";
            bool fellBack, failed;
            lock (alarms)
            {
                fellBack = alarms.Contains(nameof(AlarmType.EncoderFallback));
                failed = alarms.Contains(nameof(AlarmType.RecordingFailed));
            }
            var files = Directory.GetFiles(dir).Where(f => new FileInfo(f).Length > 0).ToList();
            string? file = capture.LastOutputFile is { } last && File.Exists(last) && new FileInfo(last).Length > 0 ? last : files.FirstOrDefault();

            summary = profile.AudioOnly ? "solo audio" : encoder;
            if (startError is not null || failed || file is null)
            {
                outcome = "NO GRABA";
                string? reason;
                lock (logs) reason = logs.LastOrDefault(l => l.StartsWith("[Error]", StringComparison.Ordinal) || l.StartsWith("[Warning]", StringComparison.Ordinal));
                summary += $" · {(startError is not null ? startError.Message : reason ?? "sin archivo")}";
            }
            else
            {
                var expected = new RecordedFileCheck.Expected(profile, profile.TargetResolution ?? feed.Size, profile.OutputFrameRate ?? feed.Rate,
                    LayoutChannels(profile.AudioLayout), recorded) { SourceScan = feed.Scan };
                var (contents, wrong) = await RecordedFileCheck.CheckAsync(locator, file, expected);
                summary += " · " + contents;
                problems.AddRange(wrong);
                // Degradar (la cadena de un codificador por hardware que el equipo no tiene, o que no abre lo que se le pide) cambia el
                // codificador, y con él lo que sale: se anota como tal, con lo que salió.
                outcome = fellBack ? "DEGRADA" : problems.Count > 0 ? "DISTINTO" : "OK";
            }
        }
        finally
        {
            await feed.DisposeAsync();
            TryDelete(dir);
        }

        string line = $"{outcome}\t{(accepted ? "acepta" : "rechaza")}\t{c.Name}\t{summary}\t{string.Join(" | ", problems)}\t{string.Join(" ", verdict)}";
        _output.WriteLine(line.Replace('\t', '\n'));
        if (Environment.GetEnvironmentVariable("BAIOSS_COMBO_REPORT") is { Length: > 0 } report)
            lock (ReportLock) File.AppendAllText(report, line + Environment.NewLine);
        // Lo aceptado tiene que grabar lo que pide; degradar solo vale si es la cadena de un codificador por hardware.
        Assert.True(!accepted || outcome == "OK" || (outcome == "DEGRADA" && IsHardware(profile.VideoCodec)),
            $"El validador acepta «{c.Name}» y el motor: {outcome} · {summary} · {string.Join(" | ", problems)}");
    }

    private static bool IsHardware(VideoCodec codec) => codec is VideoCodec.H264Nvenc or VideoCodec.HevcNvenc or VideoCodec.Av1Nvenc
        or VideoCodec.H264Qsv or VideoCodec.HevcQsv or VideoCodec.H264Amf;

    private static int LayoutChannels(AudioLayout layout) => layout switch
    {
        AudioLayout.Mono => 1, AudioLayout.Surround51 => 6, AudioLayout.Surround71 => 8, _ => 2,
    };

    // --- Las combinaciones ---

    /// <summary>Un perfil que graba con cada códec: 1080p25 a 8 Mbps en su contenedor y con su audio de siempre.</summary>
    private static RecordingProfile Base(VideoCodec codec) => new()
    {
        Name = codec.ToString(), VideoCodec = codec, HwAccel = HwAccel.None,
        TargetResolution = new Resolution(1920, 1080), OutputFrameRate = FrameRate.P25, GopSize = 25,
        VideoBitrate = Bitrate.FromMbps(codec is VideoCodec.DnxHd ? 185 : 8), RateControl = RateControlMode.ConstantBitrate,
        EncoderProfile = codec switch { VideoCodec.ProRes => EncoderProfile.ProResHq, VideoCodec.DnxHr => EncoderProfile.DnxHrHq, _ => EncoderProfile.Auto },
        Container = codec switch
        {
            VideoCodec.ProRes or VideoCodec.DnxHr or VideoCodec.DnxHd => ContainerFormat.Mov,
            VideoCodec.Mpeg2Video => ContainerFormat.Ts,
            _ => ContainerFormat.Mp4,
        },
        AudioCodec = codec is VideoCodec.Mpeg2Video ? AudioCodec.Mp2 : AudioCodec.Aac,
        AudioLayout = AudioLayout.Stereo, AudioBitrate = Bitrate.FromKbps(256), AudioSampleRate = 48_000,
    };

    private static RecordingProfile With(RecordingProfile p, Action<RecordingProfile> change) { change(p); return p; }

    private static LiveFeed Feed1080(ScanType scan = ScanType.Progressive) => new(1920, 1080, FrameRate.P25, scan);

    private static string F(FrameRate r) => string.Create(CultureInfo.InvariantCulture, $"{r.Numerator}/{r.Denominator}");

    private static IEnumerable<Case> BuildCases()
    {
        var video = Enum.GetValues<VideoCodec>();
        var longGop = new[] { VideoCodec.H264x264, VideoCodec.H265x265, VideoCodec.H264Nvenc, VideoCodec.HevcNvenc, VideoCodec.H264Qsv,
            VideoCodec.HevcQsv, VideoCodec.Mpeg2Video };

        // 1) Formato de píxel, con cada códec.
        foreach (var codec in video)
            foreach (var pix in Enum.GetValues<PixelFormat>())
                yield return new($"píxel · {codec} · {pix}", () => With(Base(codec), p => p.PixelFormat = pix), () => Feed1080());

        // 2) Barrido entrelazado, con una fuente entrelazada del mismo orden.
        foreach (var codec in video)
            foreach (var scan in new[] { ScanType.InterlacedTff, ScanType.InterlacedBff })
                yield return new($"barrido · {codec} · {scan}", () => With(Base(codec), p => p.ScanType = scan), () => Feed1080(scan));

        // 3) Perfil intra: cada uno con su códec y con el de la otra familia; y en conflicto con un formato de píxel explícito.
        foreach (var codec in new[] { VideoCodec.ProRes, VideoCodec.DnxHr, VideoCodec.DnxHd })
            foreach (var ep in Enum.GetValues<EncoderProfile>())
                yield return new($"perfil · {codec} · {ep}", () => With(Base(codec), p => p.EncoderProfile = ep), () => Feed1080());
        foreach (var (codec, ep, pix) in new[]
        {
            (VideoCodec.ProRes, EncoderProfile.ProRes4444, PixelFormat.Yuv422p10le),
            (VideoCodec.ProRes, EncoderProfile.ProResHq, PixelFormat.Yuv444p10le),
            (VideoCodec.ProRes, EncoderProfile.ProResProxy, PixelFormat.Yuv420p),
            (VideoCodec.DnxHr, EncoderProfile.DnxHrHq, PixelFormat.Yuv422p10le),
            (VideoCodec.DnxHr, EncoderProfile.DnxHrHqx, PixelFormat.Yuv422p),
            (VideoCodec.DnxHr, EncoderProfile.DnxHr444, PixelFormat.Yuv422p),
            (VideoCodec.DnxHr, EncoderProfile.DnxHrLb, PixelFormat.Yuv444p10le),
            (VideoCodec.DnxHr, EncoderProfile.DnxHrSq, PixelFormat.Yuv420p10le),
            (VideoCodec.DnxHd, EncoderProfile.DnxHrHqx, PixelFormat.Yuv422p),
        })
            yield return new($"perfil×píxel · {codec} · {ep} · {pix}", () => With(Base(codec), p => { p.EncoderProfile = ep; p.PixelFormat = pix; }), () => Feed1080());

        // 4) Control de tasa: VBR y calidad constante, con y sin bitrate; calidad en los extremos.
        foreach (var codec in longGop.Append(VideoCodec.H264Amf))
            foreach (var rc in new[] { RateControlMode.VariableBitrate, RateControlMode.ConstantQuality })
                foreach (double mbps in new[] { 0.0, 8.0 })
                    yield return new($"tasa · {codec} · {rc} · {mbps} Mbps",
                        () => With(Base(codec), p => { p.RateControl = rc; p.VideoBitrate = Bitrate.FromMbps(mbps); p.MaxBitrate = null; }), () => Feed1080());
        foreach (var codec in new[] { VideoCodec.H264x264, VideoCodec.H265x265, VideoCodec.H264Nvenc, VideoCodec.HevcNvenc })
            foreach (int q in new[] { 0, 51 })
                yield return new($"calidad · {codec} · {q}",
                    () => With(Base(codec), p => { p.RateControl = RateControlMode.ConstantQuality; p.Quality = q; }), () => Feed1080());

        // 5) Tamaños: los de broadcast, los que algún codificador no abre y los pequeños. Sin 8K: por CPU ningún códec llega a tiempo
        //    real en este equipo (DNxHR graba 0,8 s de cada 3) y eso no es la combinación; NVENC no pasa de 4096 y degrada a QuickSync.
        var sizes = new[] { (4096, 2160), (3840, 2160), (1440, 1080), (1280, 720), (720, 576), (640, 360), (320, 240),
            (160, 90), (64, 64), (1922, 1082) };
        foreach (var codec in longGop.Concat(new[] { VideoCodec.ProRes, VideoCodec.DnxHr, VideoCodec.DnxHd }))
            foreach (var (w, h) in sizes)
                yield return new($"tamaño · {codec} · {w}x{h}", () => With(Base(codec), p => p.TargetResolution = new Resolution(w, h)), () => Feed1080());
        // …y entrelazados con un alto que no es múltiplo de 4 (cada campo con un número impar de líneas).
        foreach (var codec in new[] { VideoCodec.H264x264, VideoCodec.Mpeg2Video, VideoCodec.ProRes })
            yield return new($"tamaño · {codec} · 720x578 entrelazado",
                () => With(Base(codec), p => { p.TargetResolution = new Resolution(720, 578); p.ScanType = ScanType.InterlacedTff; }),
                () => Feed1080(ScanType.InterlacedTff));

        // 6) Cadencias: las de broadcast, las que no lo son y lo que deja escribir el editor (el numerador, con el denominador de
        //    un preset 29.97 duplicado: 25/1001).
        var rates = new[] { new FrameRate(1, 1), new FrameRate(10, 1), new FrameRate(12, 1), new FrameRate(15, 1), new FrameRate(24000, 1001),
            new FrameRate(24, 1), new FrameRate(25, 1), new FrameRate(30000, 1001), new FrameRate(30, 1), new FrameRate(48, 1),
            new FrameRate(50, 1), new FrameRate(60000, 1001), new FrameRate(60, 1), new FrameRate(100, 1), new FrameRate(120000, 1001),
            new FrameRate(120, 1), new FrameRate(25, 1001) };
        foreach (var (codec, container) in new[]
        {
            (VideoCodec.Mpeg2Video, ContainerFormat.Ts), (VideoCodec.Mpeg2Video, ContainerFormat.Mxf), (VideoCodec.Mpeg2Video, ContainerFormat.ProgramStream),
            (VideoCodec.H264x264, ContainerFormat.Mp4), (VideoCodec.H264x264, ContainerFormat.Mxf), (VideoCodec.ProRes, ContainerFormat.Mxf),
            (VideoCodec.DnxHr, ContainerFormat.Mxf), (VideoCodec.DnxHd, ContainerFormat.Mxf), (VideoCodec.H264Nvenc, ContainerFormat.Mp4),
        })
            foreach (var rate in rates)
                yield return new($"cadencia · {codec} · {container} · {F(rate)}",
                    () => With(Base(codec), p =>
                    {
                        p.Container = container;
                        p.OutputFrameRate = rate;
                        if (container is ContainerFormat.Mxf) p.AudioCodec = AudioCodec.Pcm;
                    }), () => Feed1080());

        // 7) Distribución del audio con cada códec, en un contenedor que lo lleva.
        foreach (var (audio, container) in new[]
        {
            (AudioCodec.Pcm, ContainerFormat.Mkv), (AudioCodec.Pcm, ContainerFormat.Mxf), (AudioCodec.Pcm, ContainerFormat.Avi), (AudioCodec.Pcm, ContainerFormat.Mov),
            (AudioCodec.Aac, ContainerFormat.Mp4), (AudioCodec.Aac, ContainerFormat.Ts), (AudioCodec.FdkAac, ContainerFormat.Mp4),
            (AudioCodec.Opus, ContainerFormat.Mkv), (AudioCodec.Opus, ContainerFormat.Mp4), (AudioCodec.Opus, ContainerFormat.Ts),
            (AudioCodec.Mp3, ContainerFormat.Avi), (AudioCodec.Mp3, ContainerFormat.Mp4), (AudioCodec.Mp2, ContainerFormat.Ts),
            (AudioCodec.Mp2, ContainerFormat.ProgramStream),
        })
            foreach (var layout in new[] { AudioLayout.Mono, AudioLayout.Surround51, AudioLayout.Surround71 })
                yield return new($"audio · {audio} · {container} · {layout}", () => With(Base(VideoCodec.H264x264), p =>
                {
                    if (container is ContainerFormat.Mxf or ContainerFormat.ProgramStream) p.VideoCodec = VideoCodec.Mpeg2Video;
                    p.Container = container;
                    p.AudioCodec = audio;
                    p.AudioLayout = layout;
                    p.AudioBitrate = Bitrate.FromKbps(audio is AudioCodec.Mp2 or AudioCodec.Mp3 ? 192 : 256);
                }), () => Feed1080());

        // 8) Bitrate del audio en los extremos. Opus de 12 kb/s y no de 6: a 6 y 8 kb/s el decodificador de FFmpeg repite la marca
        //    del último bloque (también con un archivo hecho con FFmpeg a pelo; los paquetes son correlativos).
        foreach (var (audio, layout, kbps, container) in new[]
        {
            (AudioCodec.Opus, AudioLayout.Mono, 320, ContainerFormat.Mkv), (AudioCodec.Opus, AudioLayout.Stereo, 520, ContainerFormat.Mkv),
            (AudioCodec.Opus, AudioLayout.Surround51, 1600, ContainerFormat.Mkv), (AudioCodec.Opus, AudioLayout.Stereo, 12, ContainerFormat.Mkv),
            (AudioCodec.Aac, AudioLayout.Stereo, 1000, ContainerFormat.Mp4), (AudioCodec.Aac, AudioLayout.Surround71, 64, ContainerFormat.Mp4),
            (AudioCodec.Aac, AudioLayout.Mono, 8, ContainerFormat.Mp4),
            (AudioCodec.FdkAac, AudioLayout.Stereo, 1000, ContainerFormat.Mp4), (AudioCodec.FdkAac, AudioLayout.Mono, 8, ContainerFormat.Mp4),
            (AudioCodec.Mp3, AudioLayout.Stereo, 400, ContainerFormat.Mp4), (AudioCodec.Mp3, AudioLayout.Stereo, 8, ContainerFormat.Mp4),
            (AudioCodec.Mp3, AudioLayout.Mono, 320, ContainerFormat.Mp4),
            (AudioCodec.Mp2, AudioLayout.Mono, 384, ContainerFormat.Ts), (AudioCodec.Mp2, AudioLayout.Stereo, 32, ContainerFormat.Ts),
        })
            yield return new($"bitrate de audio · {audio} · {layout} · {kbps} kb/s", () => With(Base(VideoCodec.H264x264), p =>
            {
                p.Container = container;
                p.AudioCodec = audio;
                p.AudioLayout = layout;
                p.AudioBitrate = Bitrate.FromKbps(kbps);
            }), () => Feed1080());

        // 9) Solo audio en los contenedores de vídeo.
        foreach (var (audio, container) in new[]
        {
            (AudioCodec.Pcm, ContainerFormat.Avi), (AudioCodec.Mp3, ContainerFormat.Avi), (AudioCodec.Mp2, ContainerFormat.Avi),
            (AudioCodec.Aac, ContainerFormat.Mov), (AudioCodec.Pcm, ContainerFormat.Mov), (AudioCodec.Opus, ContainerFormat.Mkv),
            (AudioCodec.Pcm, ContainerFormat.Mkv), (AudioCodec.Aac, ContainerFormat.Ts), (AudioCodec.Mp2, ContainerFormat.Ts),
            (AudioCodec.Mp2, ContainerFormat.ProgramStream), (AudioCodec.Mp3, ContainerFormat.ProgramStream), (AudioCodec.Aac, ContainerFormat.Mp4),
            (AudioCodec.Pcm, ContainerFormat.Mp4), (AudioCodec.Opus, ContainerFormat.Mp4), (AudioCodec.Mp3, ContainerFormat.Mp4),
        })
            yield return new($"solo audio · {audio} · {container}", () => With(Base(VideoCodec.H264x264), p =>
            {
                p.AudioOnly = true;
                p.Container = container;
                p.AudioCodec = audio;
                p.AudioBitrate = Bitrate.FromKbps(audio is AudioCodec.Mp2 or AudioCodec.Mp3 ? 192 : 256);
            }), () => Feed1080());

        // 10) GOP en los extremos.
        foreach (var codec in new[] { VideoCodec.H264x264, VideoCodec.H265x265, VideoCodec.H264Nvenc, VideoCodec.HevcNvenc, VideoCodec.H264Qsv, VideoCodec.Mpeg2Video })
            foreach (int gop in new[] { 1, 300, 1000 })
                yield return new($"gop · {codec} · {gop}", () => With(Base(codec), p => p.GopSize = gop), () => Feed1080());

        // 11) Bitrate de vídeo en los extremos.
        foreach (var codec in longGop)
            foreach (double mbps in new[] { 0.1, 300 })
                yield return new($"bitrate · {codec} · {mbps} Mbps", () => With(Base(codec), p => { p.VideoBitrate = Bitrate.FromMbps(mbps); p.MaxBitrate = null; }),
                    () => Feed1080());

        // 12) DNxHD clásico en los tamaños con CID, y AV1 (solo con una NVIDIA Ada) en sus contenedores.
        foreach (var (w, h, scan, rate) in new[]
        {
            (1280, 720, ScanType.Progressive, FrameRate.P50), (1440, 1080, ScanType.InterlacedTff, FrameRate.P25),
            (960, 720, ScanType.Progressive, FrameRate.P25), (1920, 1080, ScanType.Progressive, FrameRate.P50),
        })
            yield return new($"dnxhd · {w}x{h} {scan} {F(rate)}", () => With(Base(VideoCodec.DnxHd), p =>
            {
                p.TargetResolution = new Resolution(w, h);
                p.ScanType = scan;
                p.OutputFrameRate = rate;
                p.VideoBitrate = Bitrate.FromMbps(0);
            }), () => scan is ScanType.Progressive ? Feed1080() : Feed1080(scan));
        foreach (var container in new[] { ContainerFormat.Mp4, ContainerFormat.Mkv, ContainerFormat.Avi })
            yield return new($"av1 · {container}", () => With(Base(VideoCodec.Av1Nvenc), p => p.Container = container), () => Feed1080());
    }

    private static void TryDelete(string dir)
    {
        for (int i = 0; i < 20; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch { Thread.Sleep(300); }
        }
    }
}
