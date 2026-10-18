using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Contenedor × códec de vídeo y de audio, y frecuencia de muestreo del audio, con el FFmpeg REAL y el cableado de la
/// aplicación, por las dos puertas:
/// <list type="number">
///   <item>SIN la regla: el motor de captura (<see cref="FfmpegChannelEngine"/>, que no valida) graba el perfil tal cual. Es lo
///   que hacía un canal antes: HEVC en AVI salía sin error y se releía como rawvideo; HEVC en MXF y ProRes/DNx en MP4 no
///   grababan nada; ProRes/DNx en TS/PS salían como datos privados; AAC en MXF u Opus en MOV no dejaban audio, y AAC en WAV
///   salía sin error e indecodificable; MXF a 44,1 kHz u Opus a 44,1 kHz no dejaban nada. Si un FFmpeg futuro aprende alguno,
///   esta fila falla y la regla de <c>RecordingProfileValidator</c> sobra para esa combinación.</item>
///   <item>CON la regla: el canal (<see cref="StandaloneChannelEngine"/>) rechaza el perfil en el pre-vuelo con el motivo, sin
///   arrancar la grabación ni dejar archivo, y lo deja en la auditoría.</item>
///   <item>Lo que el mensaje propone graba de verdad: el mismo códec en esos contenedores (o el audio propuesto en ese
///   contenedor, o esas frecuencias o bitrates) da un archivo legible.</item>
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

    // --- Frecuencia de muestreo del audio ---

    private static readonly int[] AacRates = { 7350, 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000 };
    private static readonly int[] Mp3Rates = { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000 };
    private static readonly int[] Mp2LsfKbps = { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
    private static readonly int[] Mp2Mpeg1Kbps = { 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 };

    /// <summary>Un perfil cuya frecuencia (o, con MP2, cuyo bitrate a esa frecuencia) rechaza el validador, y lo que propone su
    /// mensaje: frecuencias con el mismo audio en el mismo contenedor, o bitrates a la misma frecuencia.</summary>
    private sealed record RateCase(AudioCodec Audio, ContainerFormat Container, bool AudioOnly, int Rate, int Kbps,
        int[] SuggestedRates, int[] SuggestedKbps);

    /// <summary>Una fila por causa: MXF fuera de 48 kHz; el PCM de MP4, que es AAC, por encima de 96 kHz; AAC, FDK-AAC, Opus, MP3 y
    /// MP2 a una que su codificador no abre; MP3 en MP4 por debajo de 16 kHz; MP2 con un bitrate que no es de la tabla de su
    /// versión MPEG (256 kb/s, el de sus presets, a 24 kHz; 200 a 48 kHz). Con vídeo y en solo audio.</summary>
    private static readonly RateCase[] RateCases =
    {
        new(AudioCodec.Pcm, ContainerFormat.Mxf, false, 44100, 256, new[] { 48000 }, Array.Empty<int>()),
        new(AudioCodec.Pcm, ContainerFormat.Mxf, false, 96000, 256, new[] { 48000 }, Array.Empty<int>()),
        new(AudioCodec.Pcm, ContainerFormat.Mp4, false, 192000, 256, AacRates, Array.Empty<int>()),
        new(AudioCodec.Aac, ContainerFormat.Mkv, true, 192000, 256, AacRates, Array.Empty<int>()),
        new(AudioCodec.FdkAac, ContainerFormat.Ts, false, 7350, 256, AacRates[1..], Array.Empty<int>()), // FDK no abre 7350
        new(AudioCodec.Opus, ContainerFormat.Mkv, false, 44100, 256, new[] { 8000, 12000, 16000, 24000, 48000 }, Array.Empty<int>()),
        new(AudioCodec.Mp3, ContainerFormat.Mp3Audio, true, 96000, 256, Mp3Rates, Array.Empty<int>()),
        new(AudioCodec.Mp3, ContainerFormat.Mp4, false, 8000, 256, Mp3Rates[3..], Array.Empty<int>()), // de 16000 para arriba
        new(AudioCodec.Mp2, ContainerFormat.ProgramStream, false, 8000, 128, new[] { 16000, 22050, 24000, 32000, 44100, 48000 }, Array.Empty<int>()),
        new(AudioCodec.Mp2, ContainerFormat.ProgramStream, false, 24000, 256, Array.Empty<int>(), Mp2LsfKbps),
        new(AudioCodec.Mp2, ContainerFormat.Wav, true, 48000, 200, Array.Empty<int>(), Mp2Mpeg1Kbps),
    };

    /// <summary>Lo que el validador rechaza (audio, contenedor, solo audio, Hz, kb/s). Con vídeo, el codificador o el muxer no
    /// arrancan y el canal se queda «Grabando» con un archivo de 0 B; en solo audio, el motor relanza piezas de 0 B.</summary>
    public static TheoryData<AudioCodec, ContainerFormat, bool, int, int> RejectedRates()
    {
        var data = new TheoryData<AudioCodec, ContainerFormat, bool, int, int>();
        foreach (var c in RateCases) data.Add(c.Audio, c.Container, c.AudioOnly, c.Rate, c.Kbps);
        return data;
    }

    /// <summary>Lo que proponen esos mensajes, una fila por propuesta: cada frecuencia con el mismo audio, contenedor y bitrate, y
    /// cada bitrate a la misma frecuencia (<see cref="WithTheRule_TheChannelRefusesTheRate_AndRecordsNothing"/> comprueba que es
    /// lo que dice el mensaje).</summary>
    public static TheoryData<AudioCodec, ContainerFormat, bool, int, int> SuggestedRates()
    {
        var data = new TheoryData<AudioCodec, ContainerFormat, bool, int, int>();
        var rows = RateCases.SelectMany(c => c.SuggestedRates.Select(r => (c.Audio, c.Container, c.AudioOnly, Rate: r, c.Kbps))
                                     .Concat(c.SuggestedKbps.Select(k => (c.Audio, c.Container, c.AudioOnly, c.Rate, Kbps: k))));
        foreach (var r in rows.Distinct()) data.Add(r.Audio, r.Container, r.AudioOnly, r.Rate, r.Kbps);
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(RejectedRates))]
    public async Task WithoutTheRule_TheEngineLeavesNoReadableAudioAtThatRate(AudioCodec audio, ContainerFormat container, bool audioOnly, int rate, int kbps)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);

        var dir = NewDir("baioss-rmisfit");
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        var source = new LavfiLiveSource(640, 360, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = AudioProfile(audio, container, audioOnly, rate, kbps);
            await using var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger("Capture")) { OutputRoot = dir };
            var states = new List<RecordingState>();
            capture.StateChanged += (_, s) => { lock (states) states.Add(s); };
            await capture.StartPreviewAsync(source, profile, "RMI");

            Exception? startError = null;
            try { await capture.StartRecordingAsync(Guid.NewGuid(), profile, "misfit"); }
            catch (Exception ex) { startError = ex; }
            await Task.Delay(TimeSpan.FromSeconds(4));
            var stateAfter = capture.State;
            if (startError is null) await capture.StopRecordingAsync();

            var decoded = new List<(string File, long Bytes, double Seconds)>();
            foreach (var f in Directory.GetFiles(dir))
            {
                long bytes = new FileInfo(f).Length;
                decoded.Add((f, bytes, bytes == 0 ? 0 : await DecodedAudioSecondsAsync(locator.FfmpegPath, f)));
            }
            _output.WriteLine($"{audio} a {rate} Hz y {kbps} kb/s en {container}{(audioOnly ? " (solo audio)" : "")} SIN la regla ({locator.FfmpegPath}):");
            _output.WriteLine($"  arranque: {(startError is null ? "sin excepción" : startError.GetType().Name + ": " + startError.Message)}; a los 4 s: {stateAfter}");
            lock (states) _output.WriteLine($"  estados: {string.Join(" → ", states)}");
            _output.WriteLine($"  archivos: {(decoded.Count == 0 ? "ninguno" : string.Join("; ", decoded.Select(d => $"{Path.GetFileName(d.File)} ({d.Bytes} B): {d.Seconds:0.00} s de audio")))}");
            lock (logs)
                foreach (var l in logs.Where(l => l.StartsWith("[Warning]", StringComparison.Ordinal) || l.StartsWith("[Error]", StringComparison.Ordinal)).Take(4))
                    _output.WriteLine("  " + l);

            // Ningún archivo lleva audio que se pueda oír: el codificador o el muxer no arrancan y no se escribe nada.
            foreach (var d in decoded.Where(d => d.Seconds >= 1))
                Assert.Fail($"{Path.GetFileName(d.File)}: el motor ya graba {audio} a {rate} Hz y {kbps} kb/s en {container} ({d.Seconds:0.0} s); la regla sobra para esta combinación");
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(RejectedRates))]
    public async Task WithTheRule_TheChannelRefusesTheRate_AndRecordsNothing(AudioCodec audio, ContainerFormat container, bool audioOnly, int rate, int kbps)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDir("baioss-rrefused");
        var source = new LavfiLiveSource(640, 360, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = AudioProfile(audio, container, audioOnly, rate, kbps);
            var bus = new RecordingBus();
            await using var capture = new FfmpegChannelEngine(locator, new DecklinkRelayContinuityTests.CaptureLogger(_ => { })) { OutputRoot = dir };
            await capture.StartPreviewAsync(source, profile, "RRJ");
            await using var channel = new StandaloneChannelEngine("RRJ", source, profile, capture, bus: bus);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba")));
            _output.WriteLine($"{audio} a {rate} Hz y {kbps} kb/s en {container}{(audioOnly ? " (solo audio)" : "")} CON la regla: {ex.Message}");

            // Lo que ve el operador en el aviso de Grabar (y la API, y la auditoría): el motivo y qué elegir, que es lo que prueba
            // TheSuggestedRates_RecordReadableAudio.
            var expected = RateCases.Single(c => c.Audio == audio && c.Container == container && c.AudioOnly == audioOnly && c.Rate == rate && c.Kbps == kbps);
            Assert.StartsWith("Perfil de grabación inválido: ", ex.Message);
            int from = ex.Message.LastIndexOf("elige ", StringComparison.Ordinal) + "elige ".Length;
            if (expected.SuggestedKbps.Length > 0)
            {
                Assert.Contains($"El audio {audio} a {rate} Hz no se puede grabar a {kbps} kb/s: ", ex.Message);
                Assert.Equal(expected.SuggestedKbps, Numbers(ex.Message[from..ex.Message.LastIndexOf(" kb/s.", StringComparison.Ordinal)]));
            }
            else
            {
                Assert.Contains($"El audio {audio} no se puede grabar a {rate} Hz", ex.Message);
                Assert.Equal(expected.SuggestedRates, Numbers(ex.Message[from..ex.Message.LastIndexOf(" Hz.", StringComparison.Ordinal)]));
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
    [MemberData(nameof(SuggestedRates))]
    public async Task TheSuggestedRates_RecordReadableAudio(AudioCodec audio, ContainerFormat container, bool audioOnly, int rate, int kbps)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        var dir = NewDir("baioss-rfit");
        var source = new LavfiLiveSource(640, 360, FrameRate.P25);
        try
        {
            await source.OpenAsync();
            var profile = AudioProfile(audio, container, audioOnly, rate, kbps);
            await using var capture = new FfmpegChannelEngine(locator, new DecklinkRelayContinuityTests.CaptureLogger(_ => { })) { OutputRoot = dir };
            await capture.StartPreviewAsync(source, profile, "RFT");
            await using var channel = new StandaloneChannelEngine("RFT", source, profile, capture);

            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            await Task.Delay(TimeSpan.FromSeconds(4));
            await channel.StopRecordingAsync(RecordingStopReason.Operator);

            var file = capture.LastOutputFile;
            Assert.True(file is not null && File.Exists(file), $"no se generó el archivo: {file}");
            string probed = await ProbeAudioCodecAsync(locator.FfprobePath, file!);
            string probedRate = FirstLine(await RunAsync(locator.FfprobePath, "-v", "error", "-select_streams", "a:0", "-show_entries",
                "stream=sample_rate", "-of", "default=nw=1:nk=1", file!));
            double seconds = await DecodedAudioSecondsAsync(locator.FfmpegPath, file!);
            _output.WriteLine($"{audio} a {rate} Hz y {kbps} kb/s en {container}{(audioOnly ? " (solo audio)" : "")}: {Path.GetFileName(file)} " +
                              $"({new FileInfo(file!).Length} B), audio «{probed}» a {probedRate} Hz, {seconds:0.00} s decodificados");
            // PCM en MP4, MOV y TS es AAC; Opus decodifica siempre a 48 kHz y así lo declara el archivo.
            Assert.Equal(audio switch
            {
                AudioCodec.Pcm => container is ContainerFormat.Mp4 or ContainerFormat.Mov or ContainerFormat.Ts ? "aac" : "pcm_s24le",
                AudioCodec.Aac or AudioCodec.FdkAac => "aac",
                AudioCodec.Opus => "opus",
                AudioCodec.Mp3 => "mp3",
                _ => "mp2",
            }, probed);
            Assert.Equal(audio is AudioCodec.Opus ? "48000" : rate.ToString(System.Globalization.CultureInfo.InvariantCulture), probedRate);
            Assert.True(seconds >= 2, $"solo se decodificaron {seconds:0.00} s de audio de ~4 s grabados");
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    [SkippableFact]
    public async Task TheValidatorsRates_AreTheOnesThisFfmpegDeclares()
    {
        // El validador lleva la lista de cada codificador (FFmpeg las declara en «Supported sample rates» y no abre con otra). Si un
        // FFmpeg futuro la cambia, esta prueba lo dice antes que un canal «Grabando» sin archivo. En MKV, que no limita ninguna; MP2
        // a 128 kb/s, que vale a todas las suyas.
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        int[] probe = { 1000, 7350, 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 47952, 48000, 64000, 88200, 96000, 176400, 192000, 384000 };
        foreach (var audio in Enum.GetValues<AudioCodec>())
        {
            string encoder = FfmpegCodecMap.AudioEncoder(audio);
            string help = await RunAsync(locator.FfmpegPath, "-hide_banner", "-h", $"encoder={encoder}");
            Assert.Contains($"Encoder {encoder} ", help);
            var line = help.Split('\n').FirstOrDefault(l => l.Contains("Supported sample rates:", StringComparison.Ordinal));
            var declared = line is null ? null : Numbers(line[(line.IndexOf(':') + 1)..]);
            _output.WriteLine($"{audio} ({encoder}): {(declared is null ? "cualquier frecuencia" : string.Join(" ", declared))}");
            foreach (int rate in probe.Concat(declared ?? Array.Empty<int>()).Distinct())
            {
                var p = AudioProfile(audio, ContainerFormat.Mkv, audioOnly: false, rate, audio is AudioCodec.Mp2 ? 128 : 256);
                bool accepted = !RecordingProfileValidator.Validate(p).Any(e => e.Contains(" Hz", StringComparison.Ordinal));
                Assert.True(accepted == (declared?.Contains(rate) ?? true),
                    $"{audio} a {rate} Hz: el validador {(accepted ? "lo acepta" : "lo rechaza")} y {encoder} {(declared?.Contains(rate) ?? true ? "lo declara" : "no lo declara")}");
            }
        }
    }

    // --- Utilidades ---

    /// <summary>«8000, 12000, 16000, 24000 o 48000» (o «… or …», o separados por comas): los números, en orden.</summary>
    private static int[] Numbers(string list) => System.Text.RegularExpressions.Regex.Matches(list, @"\d+")
        .Select(m => int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

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
    /// todos llevan) o en solo audio: así lo único que puede fallar es el audio (o su frecuencia, o su bitrate).</summary>
    private static RecordingProfile AudioProfile(AudioCodec audio, ContainerFormat container, bool audioOnly, int rate = 48_000, int kbps = 256) => new()
    {
        Name = $"{audio} · {container}", AudioOnly = audioOnly,
        VideoCodec = container is ContainerFormat.Mxf or ContainerFormat.ProgramStream ? VideoCodec.Mpeg2Video : VideoCodec.H264x264,
        HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(4), GopSize = 25, RateControl = RateControlMode.ConstantBitrate,
        Container = container, AudioCodec = audio,
        AudioLayout = AudioLayout.Stereo, AudioBitrate = Bitrate.FromKbps(kbps), AudioSampleRate = rate,
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
