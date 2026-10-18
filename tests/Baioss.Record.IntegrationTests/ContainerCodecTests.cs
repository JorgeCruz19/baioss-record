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
/// Contenedor × códec de vídeo y de audio con el FFmpeg REAL y el cableado de la aplicación, por las dos puertas:
/// <list type="number">
///   <item>SIN la regla: el motor de captura (<see cref="FfmpegChannelEngine"/>, que no valida) graba el perfil tal cual. Es lo
///   que hacía un canal antes: HEVC en AVI salía sin error y se releía como rawvideo; HEVC en MXF y ProRes/DNx en MP4 no
///   grababan nada; ProRes/DNx en TS/PS salían como datos privados; AAC en MXF u Opus en MOV no dejaban audio, y AAC en WAV
///   salía sin error e indecodificable. Si un FFmpeg futuro aprende alguno, esta fila falla y la regla de
///   <c>RecordingProfileValidator</c> sobra para esa combinación.</item>
///   <item>CON la regla: el canal (<see cref="StandaloneChannelEngine"/>) rechaza el perfil en el pre-vuelo con el motivo, sin
///   arrancar la grabación ni dejar archivo, y lo deja en la auditoría.</item>
///   <item>Lo que el mensaje propone graba de verdad: el mismo códec en esos contenedores (o el audio propuesto en ese
///   contenedor) da un archivo legible.</item>
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

    // --- Contenedor × códec de audio ---

    /// <summary>Audio que el validador rechaza (códec, contenedor, solo audio). Con vídeo, el muxer no escribe la cabecera y el
    /// canal se queda «Grabando» con un archivo de 0 B; en solo audio, el motor relanza piezas de 0 B (Opus en AVI deja 172 B de
    /// cabecera); AAC en WAV sale sin error e indecodificable; MXF en solo audio no graba con ningún códec.</summary>
    public static TheoryData<AudioCodec, ContainerFormat, bool> RejectedAudio => new()
    {
        // MXF: solo PCM, y con vídeo.
        { AudioCodec.Aac, ContainerFormat.Mxf, false },
        { AudioCodec.FdkAac, ContainerFormat.Mxf, false },
        { AudioCodec.Opus, ContainerFormat.Mxf, false },
        { AudioCodec.Mp3, ContainerFormat.Mxf, false },
        { AudioCodec.Mp2, ContainerFormat.Mxf, false },
        { AudioCodec.Pcm, ContainerFormat.Mxf, true },
        { AudioCodec.Aac, ContainerFormat.Mxf, true },
        { AudioCodec.FdkAac, ContainerFormat.Mxf, true },
        { AudioCodec.Opus, ContainerFormat.Mxf, true },
        { AudioCodec.Mp3, ContainerFormat.Mxf, true },
        { AudioCodec.Mp2, ContainerFormat.Mxf, true },
        // PS: solo MP2 y MP3.
        { AudioCodec.Pcm, ContainerFormat.ProgramStream, false },
        { AudioCodec.Aac, ContainerFormat.ProgramStream, false },
        { AudioCodec.FdkAac, ContainerFormat.ProgramStream, false },
        { AudioCodec.Opus, ContainerFormat.ProgramStream, false },
        { AudioCodec.Pcm, ContainerFormat.ProgramStream, true },
        { AudioCodec.Aac, ContainerFormat.ProgramStream, true },
        { AudioCodec.FdkAac, ContainerFormat.ProgramStream, true },
        { AudioCodec.Opus, ContainerFormat.ProgramStream, true },
        // Opus en AVI, MOV y WAV.
        { AudioCodec.Opus, ContainerFormat.Avi, false },
        { AudioCodec.Opus, ContainerFormat.Mov, false },
        { AudioCodec.Opus, ContainerFormat.Avi, true },
        { AudioCodec.Opus, ContainerFormat.Mov, true },
        { AudioCodec.Opus, ContainerFormat.Wav, true },
        // FDK-AAC en AVI y WAV (lo rechaza el incluido; 8.0 lo graba) y el AAC nativo en WAV (sale indecodificable).
        { AudioCodec.FdkAac, ContainerFormat.Avi, false },
        { AudioCodec.FdkAac, ContainerFormat.Avi, true },
        { AudioCodec.FdkAac, ContainerFormat.Wav, true },
        { AudioCodec.Aac, ContainerFormat.Wav, true },
        // MP3: solo MP3.
        { AudioCodec.Pcm, ContainerFormat.Mp3Audio, true },
        { AudioCodec.Aac, ContainerFormat.Mp3Audio, true },
        { AudioCodec.FdkAac, ContainerFormat.Mp3Audio, true },
        { AudioCodec.Opus, ContainerFormat.Mp3Audio, true },
        { AudioCodec.Mp2, ContainerFormat.Mp3Audio, true },
    };

    /// <summary>El audio que el mensaje propone en cada contenedor que rechaza alguno (MXF sin «Solo audio»).</summary>
    public static TheoryData<AudioCodec, ContainerFormat, bool> SuggestedAudio => new()
    {
        { AudioCodec.Pcm, ContainerFormat.Mxf, false },
        { AudioCodec.Mp2, ContainerFormat.ProgramStream, false },
        { AudioCodec.Mp3, ContainerFormat.ProgramStream, false },
        { AudioCodec.Mp2, ContainerFormat.ProgramStream, true },
        { AudioCodec.Mp3, ContainerFormat.ProgramStream, true },
        { AudioCodec.Pcm, ContainerFormat.Avi, false },
        { AudioCodec.Mp3, ContainerFormat.Avi, false },
        { AudioCodec.Mp2, ContainerFormat.Avi, false },
        { AudioCodec.Pcm, ContainerFormat.Avi, true },
        { AudioCodec.Aac, ContainerFormat.Mov, false },
        { AudioCodec.Aac, ContainerFormat.Mov, true },
        { AudioCodec.Pcm, ContainerFormat.Wav, true },
        { AudioCodec.Mp3, ContainerFormat.Mp3Audio, true },
    };

    [SkippableTheory]
    [MemberData(nameof(RejectedAudio))]
    public async Task WithoutTheRule_TheEngineLeavesNoReadableAudio(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);

        var dir = NewDir("baioss-amisfit");
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        var source = new LavfiLiveSource(640, 360, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = AudioProfile(audio, container, audioOnly);
            await using var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger("Capture")) { OutputRoot = dir };
            var states = new List<RecordingState>();
            var interruptions = new List<string>();
            capture.StateChanged += (_, s) => { lock (states) states.Add(s); };
            capture.RecordingInterrupted += (_, r) => { lock (interruptions) interruptions.Add($"salida {r.ExitCode}"); };
            await capture.StartPreviewAsync(source, profile, "AMI");

            Exception? startError = null;
            try { await capture.StartRecordingAsync(Guid.NewGuid(), profile, "misfit"); }
            catch (Exception ex) { startError = ex; }
            await Task.Delay(TimeSpan.FromSeconds(4));
            var stateAfter = capture.State;
            if (startError is null) await capture.StopRecordingAsync();

            var files = Directory.GetFiles(dir);
            var decoded = new List<(string File, long Bytes, string Codec, double Seconds)>();
            foreach (var f in files)
            {
                long bytes = new FileInfo(f).Length;
                decoded.Add(bytes == 0 ? (f, 0, "", 0)
                    : (f, bytes, await ProbeAudioCodecAsync(locator.FfprobePath, f), await DecodedAudioSecondsAsync(locator.FfmpegPath, f)));
            }
            _output.WriteLine($"{audio} en {container}{(audioOnly ? " (solo audio)" : "")} SIN la regla ({locator.FfmpegPath}):");
            _output.WriteLine($"  arranque: {(startError is null ? "sin excepción" : startError.GetType().Name + ": " + startError.Message)}; a los 4 s: {stateAfter}");
            lock (states) _output.WriteLine($"  estados: {string.Join(" → ", states)}");
            lock (interruptions) _output.WriteLine($"  interrupciones: {(interruptions.Count == 0 ? "ninguna" : string.Join(", ", interruptions))}");
            _output.WriteLine($"  archivos: {(decoded.Count == 0 ? "ninguno" : string.Join("; ", decoded.Select(d => $"{Path.GetFileName(d.File)} ({d.Bytes} B): audio «{d.Codec}», {d.Seconds:0.00} s decodificados")))}");
            lock (logs)
                foreach (var l in logs.Where(l => l.StartsWith("[Warning]", StringComparison.Ordinal) || l.StartsWith("[Error]", StringComparison.Ordinal)).Take(4))
                    _output.WriteLine("  " + l);

            // Ningún archivo lleva audio que se pueda oír: o no hay archivo, o está vacío o con la cabecera a medias, o el
            // decodificador no saca ni un segundo de los ~4 grabados (AAC en WAV: un par de paquetes y luego «Invalid data»).
            foreach (var d in decoded.Where(d => d.Seconds >= 1))
            {
                // FDK-AAC en AVI y WAV: FFmpeg 8.0 lo graba legible y el incluido (git-2026-06-14) lo rechaza; la regla sigue por él.
                Skip.If(audio is AudioCodec.FdkAac && container is ContainerFormat.Avi or ContainerFormat.Wav,
                    $"Este FFmpeg graba FDK-AAC en {container} legible ({d.Seconds:0.0} s); el incluido lo rechaza («ADTS is only supported with codec tag 0x1610»).");
                Assert.Fail($"{Path.GetFileName(d.File)}: el motor ya graba {audio} legible en {container} ({d.Seconds:0.0} s de audio «{d.Codec}»); la regla sobra para esta combinación");
            }
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(RejectedAudio))]
    public async Task WithTheRule_TheChannelRefusesTheAudio_AndRecordsNothing(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDir("baioss-arefused");
        var source = new LavfiLiveSource(640, 360, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = AudioProfile(audio, container, audioOnly);
            var bus = new RecordingBus();
            await using var capture = new FfmpegChannelEngine(locator, new DecklinkRelayContinuityTests.CaptureLogger(_ => { })) { OutputRoot = dir };
            await capture.StartPreviewAsync(source, profile, "ARJ");
            await using var channel = new StandaloneChannelEngine("ARJ", source, profile, capture, bus: bus);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba")));
            _output.WriteLine($"{audio} en {container}{(audioOnly ? " (solo audio)" : "")} CON la regla: {ex.Message}");

            // Lo que ve el operador en el aviso de Grabar (y la API, y la auditoría): el motivo y qué elegir.
            Assert.StartsWith("Perfil de grabación inválido: ", ex.Message);
            if (audioOnly && container is ContainerFormat.Mxf)
                Assert.Contains("El contenedor Mxf necesita una pista de vídeo", ex.Message);
            else
            {
                Assert.Contains($"El audio {audio} no se puede grabar en {container}", ex.Message);
                Assert.Contains($"En {container}, elige el audio", ex.Message);
            }
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
    [MemberData(nameof(SuggestedAudio))]
    public async Task TheSuggestedAudio_RecordsReadableAudio(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDir("baioss-afit");
        var source = new LavfiLiveSource(640, 360, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = AudioProfile(audio, container, audioOnly);
            await using var capture = new FfmpegChannelEngine(locator, new DecklinkRelayContinuityTests.CaptureLogger(_ => { })) { OutputRoot = dir };
            await capture.StartPreviewAsync(source, profile, "AFT");
            await using var channel = new StandaloneChannelEngine("AFT", source, profile, capture);

            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            await Task.Delay(TimeSpan.FromSeconds(4));
            await channel.StopRecordingAsync(RecordingStopReason.Operator);

            var file = capture.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), $"no se generó el archivo: {file}");
            string probed = await ProbeAudioCodecAsync(locator.FfprobePath, file!);
            double seconds = await DecodedAudioSecondsAsync(locator.FfmpegPath, file!);
            _output.WriteLine($"{audio} en {container}{(audioOnly ? " (solo audio)" : "")}: {Path.GetFileName(file)} ({new FileInfo(file!).Length} B), audio «{probed}», {seconds:0.00} s decodificados");
            Assert.Equal(ExpectedAudioCodecName(audio), probed);
            Assert.True(seconds >= 2, $"solo se decodificaron {seconds:0.00} s de audio de ~4 s grabados");
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

    /// <summary>Perfil con ese audio en ese contenedor, con el vídeo de sus presets (MPEG-2 en MXF y PS, H.264 en el resto, que
    /// todos llevan) o en solo audio: así lo único que puede fallar es el audio.</summary>
    private static RecordingProfile AudioProfile(AudioCodec audio, ContainerFormat container, bool audioOnly) => new()
    {
        Name = $"{audio} · {container}", AudioOnly = audioOnly,
        VideoCodec = container is ContainerFormat.Mxf or ContainerFormat.ProgramStream ? VideoCodec.Mpeg2Video : VideoCodec.H264x264,
        HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(4), GopSize = 25, RateControl = RateControlMode.ConstantBitrate,
        Container = container, AudioCodec = audio,
        AudioLayout = AudioLayout.Stereo, AudioBitrate = Bitrate.FromKbps(256), AudioSampleRate = 48_000,
    };

    /// <summary>Lo que ffprobe dice del audio propuesto (en esos contenedores el PCM sigue siendo PCM: solo MP4, MOV y TS lo
    /// pasan a AAC).</summary>
    private static string ExpectedAudioCodecName(AudioCodec audio) => audio switch
    {
        AudioCodec.Pcm => "pcm_s24le",
        AudioCodec.Aac => "aac",
        AudioCodec.Mp3 => "mp3",
        AudioCodec.Mp2 => "mp2",
        _ => throw new ArgumentOutOfRangeException(nameof(audio)),
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

    /// <summary>Códec del primer audio, o "" si no hay.</summary>
    private static async Task<string> ProbeAudioCodecAsync(string ffprobe, string file)
        => FirstLine(await RunAsync(ffprobe, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", file));

    /// <summary>Segundos de audio que el decodificador saca del primer audio (0 si no hay audio o no se puede decodificar). El
    /// volumedetect cuenta las muestras; FFmpeg 8.0 configura el grafo dos veces y el primero informa «n_samples: 0»: vale el
    /// último.</summary>
    private static async Task<double> DecodedAudioSecondsAsync(string ffmpeg, string file)
    {
        var (_, log) = await RunWithLogAsync(ffmpeg, "-hide_banner", "-nostats", "-i", file, "-map", "0:a:0",
            "-af", "aformat=sample_rates=48000:channel_layouts=stereo,volumedetect", "-f", "null", "-");
        var samples = System.Text.RegularExpressions.Regex.Matches(log, @"n_samples: (\d+)").LastOrDefault();
        return samples is null ? 0 : long.Parse(samples.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / 2.0 / 48_000;
    }

    private static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static async Task<string> RunAsync(string exe, params string[] args) => (await RunWithLogAsync(exe, args)).Output;

    /// <summary>La salida estándar y la de errores (donde FFmpeg escribe su registro).</summary>
    private static async Task<(string Output, string Log)> RunWithLogAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await stdout, await stderr);
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
