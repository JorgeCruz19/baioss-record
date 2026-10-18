using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Localization;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Contenedor × códec de vídeo con el FFmpeg REAL y el cableado de la aplicación, por las dos puertas:
/// <list type="number">
///   <item>SIN la regla: el motor de captura (<see cref="FfmpegChannelEngine"/>, que no valida) graba el perfil tal cual. Es lo
///   que hacía un canal antes: HEVC en AVI salía sin error y se releía como rawvideo; HEVC en MXF y ProRes/DNx en MP4 no
///   grababan nada; ProRes/DNx en TS/PS salían como datos privados. Si un FFmpeg futuro aprende alguno, esta fila falla y la
///   regla de <c>RecordingProfileValidator</c> sobra para esa combinación.</item>
///   <item>CON la regla: el canal (<see cref="StandaloneChannelEngine"/>) rechaza el perfil en el pre-vuelo con el motivo, sin
///   arrancar la grabación ni dejar archivo, y lo deja en la auditoría.</item>
///   <item>Lo que el mensaje propone graba de verdad: el mismo códec en esos contenedores da un archivo legible.</item>
/// </list>
/// </summary>
[Collection("Localizer")] // el motivo del rechazo sale en el idioma de la aplicación (estado global)
public sealed class ContainerCodecTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly AppLanguage _language = Localizer.Language;

    public ContainerCodecTests(ITestOutputHelper output)
    {
        _output = output;
        Localizer.Language = AppLanguage.Spanish;
    }

    public void Dispose() => Localizer.Language = _language;

    /// <summary>Combinaciones que el validador rechaza, con lo que hace FFmpeg con ellas. AV1 (Av1Nvenc) no figura en la
    /// puerta «sin la regla»: av1_nvenc solo abre en una NVIDIA Ada, y sin ella el canal degrada a libx264 y graba H.264.</summary>
    public static TheoryData<VideoCodec, ContainerFormat> Rejected => new()
    {
        { VideoCodec.H265x265, ContainerFormat.Avi },
        { VideoCodec.HevcQsv, ContainerFormat.Avi },
        { VideoCodec.HevcNvenc, ContainerFormat.Avi },
        { VideoCodec.H265x265, ContainerFormat.Mxf },
        { VideoCodec.HevcQsv, ContainerFormat.Mxf },
        { VideoCodec.HevcNvenc, ContainerFormat.Mxf },
        { VideoCodec.ProRes, ContainerFormat.Mp4 },
        { VideoCodec.DnxHr, ContainerFormat.Mp4 },
        { VideoCodec.ProRes, ContainerFormat.Ts },
        { VideoCodec.DnxHd, ContainerFormat.Ts },
        { VideoCodec.ProRes, ContainerFormat.ProgramStream },
        { VideoCodec.DnxHr, ContainerFormat.ProgramStream },
        { VideoCodec.Av1Nvenc, ContainerFormat.Mxf },
        { VideoCodec.Av1Nvenc, ContainerFormat.Mov },
        { VideoCodec.Av1Nvenc, ContainerFormat.Ts },
        { VideoCodec.Av1Nvenc, ContainerFormat.ProgramStream },
    };

    /// <summary>Los contenedores que el mensaje propone para cada familia.</summary>
    public static TheoryData<VideoCodec, ContainerFormat> Suggested => new()
    {
        { VideoCodec.H265x265, ContainerFormat.Mp4 },
        { VideoCodec.H265x265, ContainerFormat.Mov },
        { VideoCodec.H265x265, ContainerFormat.Mkv },
        { VideoCodec.H265x265, ContainerFormat.Ts },
        { VideoCodec.HevcQsv, ContainerFormat.Mkv },
        { VideoCodec.HevcNvenc, ContainerFormat.Mp4 },
        { VideoCodec.ProRes, ContainerFormat.Mov },
        { VideoCodec.ProRes, ContainerFormat.Mxf },
        { VideoCodec.ProRes, ContainerFormat.Mkv },
        { VideoCodec.DnxHr, ContainerFormat.Mxf },
        { VideoCodec.DnxHd, ContainerFormat.Mov },
        { VideoCodec.DnxHr, ContainerFormat.Mkv },
        { VideoCodec.Av1Nvenc, ContainerFormat.Mp4 },
        { VideoCodec.Av1Nvenc, ContainerFormat.Mkv },
    };

    [SkippableTheory]
    [MemberData(nameof(Rejected))]
    public async Task WithoutTheRule_FfmpegLeavesNoReadableVideo(VideoCodec codec, ContainerFormat container)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        await SkipIfTheFamilyCannotBeEncodedAsync(locator, codec);

        var dir = NewDir("baioss-misfit");
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        var source = new LavfiLiveSource(1280, 720, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = Profile(codec, container);
            await using var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger("Capture")) { OutputRoot = dir };
            var states = new List<RecordingState>();
            var alarms = new List<string>();
            var interruptions = new List<string>();
            capture.StateChanged += (_, s) => { lock (states) states.Add(s); };
            capture.AlarmChanged += (_, a) => { lock (alarms) alarms.Add($"{a.Type}={(a.Active ? "on" : "off")}"); };
            capture.RecordingInterrupted += (_, r) => { lock (interruptions) interruptions.Add($"salida {r.ExitCode}: {r.Reason}"); };
            await capture.StartPreviewAsync(source, profile, "MIS");

            Exception? startError = null;
            try { await capture.StartRecordingAsync(Guid.NewGuid(), profile, "misfit"); }
            catch (Exception ex) { startError = ex; }
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (startError is null) await capture.StopRecordingAsync();

            var files = Directory.GetFiles(dir);
            var probes = new List<string>();
            foreach (var f in files)
                probes.Add($"{Path.GetFileName(f)} ({new FileInfo(f).Length} B): vídeo «{await ProbeVideoCodecAsync(locator.FfprobePath, f)}»");
            _output.WriteLine($"{codec} en {container} SIN la regla ({Path.GetFileName(locator.FfmpegPath)} de {locator.FfmpegPath}):");
            _output.WriteLine($"  arranque: {(startError is null ? "sin excepción" : startError.GetType().Name + ": " + startError.Message)}");
            lock (states) _output.WriteLine($"  estados: {string.Join(" → ", states)}");
            lock (alarms) _output.WriteLine($"  alarmas: {(alarms.Count == 0 ? "ninguna" : string.Join(", ", alarms))}");
            lock (interruptions) _output.WriteLine($"  interrupciones: {(interruptions.Count == 0 ? "ninguna" : string.Join(" | ", interruptions))}");
            _output.WriteLine($"  archivos: {(probes.Count == 0 ? "ninguno" : string.Join("; ", probes))}");
            lock (logs)
                foreach (var l in logs.Where(l => l.StartsWith("[Warning]", StringComparison.Ordinal) || l.StartsWith("[Error]", StringComparison.Ordinal)).Take(8))
                    _output.WriteLine("  " + l);

            // Ningún archivo lleva vídeo de esa familia que se pueda leer: o no hay archivo, o está vacío, o ffprobe ve otra cosa
            // (rawvideo, datos privados) o el decodificador no saca ni un cuadro.
            string expected = ExpectedCodecName(codec);
            foreach (var f in files.Where(f => new FileInfo(f).Length > 0))
                if (await ProbeVideoCodecAsync(locator.FfprobePath, f) == expected)
                    Assert.True(await DecodedFramesAsync(locator.FfprobePath, f) == 0,
                        $"{Path.GetFileName(f)}: FFmpeg ya graba {expected} legible en {container}; la regla sobra para esta combinación");
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Rejected))]
    public async Task WithTheRule_TheChannelRefusesTheProfile_AndRecordsNothing(VideoCodec codec, ContainerFormat container)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDir("baioss-refused");
        var source = new LavfiLiveSource(1280, 720, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = Profile(codec, container);
            var bus = new RecordingBus();
            await using var capture = new FfmpegChannelEngine(locator, new DecklinkRelayContinuityTests.CaptureLogger(_ => { })) { OutputRoot = dir };
            await capture.StartPreviewAsync(source, profile, "REJ");
            await using var channel = new StandaloneChannelEngine("REJ", source, profile, capture, bus: bus);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba")));
            _output.WriteLine($"{codec} en {container} CON la regla: {ex.Message}");

            // Lo que ve el operador en el aviso de Grabar (y la API, y la auditoría): el motivo y qué elegir.
            Assert.StartsWith("Perfil de grabación inválido: ", ex.Message);
            Assert.Contains($"no se puede grabar en {container}", ex.Message);
            Assert.Contains(codec.ToString(), ex.Message);
            Assert.Contains("elige", ex.Message);
            // Ni grabación ni archivo: el pre-vuelo corre antes de crear la sesión y de tocar FFmpeg; el canal sigue en reposo.
            Assert.Equal(RecordingState.Idle, capture.State);
            Assert.Null(channel.Status.SessionId);
            Assert.Empty(Directory.GetFiles(dir));
            var audited = Assert.Single(bus.Events.OfType<RecordingStartFailed>());
            Assert.Equal(ex.Message, audited.Reason);
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Suggested))]
    public async Task TheSuggestedContainers_RecordAReadableFile(VideoCodec codec, ContainerFormat container)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        await SkipIfTheFamilyCannotBeEncodedAsync(locator, codec);

        var dir = NewDir("baioss-fit");
        var source = new LavfiLiveSource(1280, 720, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = Profile(codec, container);
            await using var capture = new FfmpegChannelEngine(locator, new DecklinkRelayContinuityTests.CaptureLogger(_ => { })) { OutputRoot = dir };
            await capture.StartPreviewAsync(source, profile, "FIT");
            await using var channel = new StandaloneChannelEngine("FIT", source, profile, capture);

            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            await Task.Delay(TimeSpan.FromSeconds(4));
            await channel.StopRecordingAsync(RecordingStopReason.Operator);

            var file = capture.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), $"no se generó el archivo: {file}");
            string probed = await ProbeVideoCodecAsync(locator.FfprobePath, file!);
            long frames = await DecodedFramesAsync(locator.FfprobePath, file!);
            _output.WriteLine($"{codec} en {container}: {Path.GetFileName(file)} ({new FileInfo(file!).Length} B), vídeo «{probed}», {frames} cuadros decodificados");
            Assert.Equal(ExpectedCodecName(codec), probed);
            Assert.True(frames >= 25, $"solo se decodificaron {frames} cuadros de ~4 s a 25 fps");
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    // --- Utilidades ---

    /// <summary>Perfil por bitrate con un audio que el contenedor admite (MXF solo PCM; el PS de FFmpeg no acepta AAC ni PCM
    /// s24le): así lo único que puede fallar es el vídeo.</summary>
    private static RecordingProfile Profile(VideoCodec codec, ContainerFormat container) => new()
    {
        Name = $"{codec} · {container}", VideoCodec = codec, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(4), GopSize = 25, RateControl = RateControlMode.ConstantBitrate,
        Container = container,
        AudioCodec = container switch { ContainerFormat.Mxf => AudioCodec.Pcm, ContainerFormat.ProgramStream => AudioCodec.Mp2, _ => AudioCodec.Aac },
        AudioLayout = AudioLayout.Stereo, AudioBitrate = Bitrate.FromKbps(192), AudioSampleRate = 48_000,
    };

    private static string ExpectedCodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H265x265 or VideoCodec.HevcQsv or VideoCodec.HevcNvenc => "hevc",
        VideoCodec.Av1Nvenc => "av1",
        VideoCodec.ProRes => "prores",
        VideoCodec.DnxHd or VideoCodec.DnxHr => "dnxhd",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    /// <summary>AV1 solo lo codifica av1_nvenc (NVIDIA Ada): sin ella el canal degrada a libx264 y sale H.264, que no prueba
    /// nada de AV1. HEVC degrada dentro de HEVC (NVENC → QuickSync → libx265): esas filas valen en cualquier equipo.</summary>
    private static async Task SkipIfTheFamilyCannotBeEncodedAsync(FfmpegLocator locator, VideoCodec codec)
    {
        if (codec is not VideoCodec.Av1Nvenc) return;
        var (ok, detail) = await locator.ProbeVideoEncoderAsync("av1_nvenc");
        Skip.IfNot(ok, $"av1_nvenc no abre en este equipo ({detail}).");
    }

    /// <summary>Códec del primer vídeo, o "" si no hay. Primera línea: en un TS ffprobe lista el flujo dos veces (en su
    /// programa y suelto).</summary>
    private static async Task<string> ProbeVideoCodecAsync(string ffprobe, string file)
        => FirstLine(await RunAsync(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", file));

    /// <summary>Cuadros que el decodificador saca del primer vídeo (0 si no hay vídeo o no se puede decodificar).</summary>
    private static async Task<long> DecodedFramesAsync(string ffprobe, string file)
    {
        var s = FirstLine(await RunAsync(ffprobe, "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames",
            "-of", "default=nw=1:nk=1", file));
        return long.TryParse(s, out var n) ? n : 0;
    }

    private static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static async Task<string> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        await stderr;
        return await stdout;
    }

    private static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        for (int i = 0; i < 20; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch { Thread.Sleep(300); } // el remux de MOV/MP4 (32 MB de DNxHD) o la verificación pueden tener aún el archivo abierto
        }
    }

    /// <summary>Bus que solo apunta lo publicado: la auditoría del intento rechazado.</summary>
    private sealed class RecordingBus : IEventBus
    {
        private readonly List<IDomainEvent> _events = new();
        public IReadOnlyList<IDomainEvent> Events { get { lock (_events) return _events.ToList(); } }

        public Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct = default)
        {
            lock (_events) _events.Add(domainEvent);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent
            => new Unsubscriber();

        private sealed class Unsubscriber : IDisposable { public void Dispose() { } }
    }
}
