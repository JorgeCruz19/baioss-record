using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Fuente de captura de red (SRT/RTMP): los argumentos exactos del receptor permanente en cada modo, la señal que la
/// fuente publica a partir de lo que el receptor ve (sin señal → emisor conectado con formato y audio → perdido →
/// contraseña rechazada), la entrada que le da al proceso del canal (el relé), la fábrica y el resolver, el parser del
/// volcado de entrada de FFmpeg y las dos opciones del supervisor de las que todo depende. Sin FFmpeg ni red: el
/// receptor aquí es falso; el real lo cubren los tests de integración en loopback.
/// </summary>
[Collection("Localizer")]
public class NetworkStreamCaptureSourceTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;
    public NetworkStreamCaptureSourceTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    private static InputSource Def(NetworkInput input, string name = "red") => input.ToInputSource(Guid.NewGuid(), name);

    private static readonly DetectedVideoMode Hd25 = new(new Resolution(1920, 1080), new FrameRate(25, 1), false);

    /// <summary>Receptor falso: la fuente solo ve sus eventos, igual que con el real.</summary>
    private sealed class FakeReceiver : INetworkStreamReceiver
    {
        public int ConsumerPort => 43210;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public event EventHandler<FfmpegInputDescription>? PeerConnected;
        public event EventHandler? PeerLost;
        public event EventHandler? PassphraseRejected;
        public Task StartAsync(CancellationToken ct = default) { Started = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Connect(DetectedVideoMode? video, bool hasAudio) => PeerConnected?.Invoke(this, new FfmpegInputDescription("srt://0.0.0.0:9000", video, hasAudio));
        public void Lose() => PeerLost?.Invoke(this, EventArgs.Empty);
        public void Reject() => PassphraseRejected?.Invoke(this, EventArgs.Empty);
    }

    private static (NetworkStreamCaptureSource Source, FakeReceiver Receiver, Func<int> Created) WithFake(NetworkInput input)
    {
        var fake = new FakeReceiver();
        int created = 0;
        var source = new NetworkStreamCaptureSource(Def(input), _ => { created++; return fake; });
        return (source, fake, () => created);
    }

    // --- Receptor permanente: argumentos exactos de FFmpeg ---

    [Fact]
    public void SrtListen_Arguments()
    {
        var input = new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000, LatencyMs = 250, Passphrase = "clave-secreta-1", StreamId = "no-en-escucha" };
        var args = NetworkStreamReceiver.InputArgumentsFor(input);

        Assert.Equal(new[]
        {
            "-mode", "listener", "-latency", "250000", "-timeout", "10000000", "-passphrase", "clave-secreta-1",
            "-i", "srt://0.0.0.0:9000",
        }, args);
        Assert.DoesNotContain("-srt_streamid", args); // el stream id lo presenta el que LLAMA
        Assert.DoesNotContain("-streamid", args);     // la opción a secas es del CLI de FFmpeg y chocaría (medido)
    }

    [Fact]
    public void SrtConnect_Arguments_WithStreamId_AndWithoutPassphrase()
    {
        var input = new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Connect, Host = "10.0.0.7", Port = 9000, StreamId = "canal1" };
        var args = NetworkStreamReceiver.InputArgumentsFor(input);

        Assert.Equal(new[] { "-mode", "caller", "-latency", "120000", "-timeout", "10000000", "-srt_streamid", "canal1", "-i", "srt://10.0.0.7:9000" }, args);
        Assert.DoesNotContain("-passphrase", args);
    }

    [Fact]
    public void RtmpListen_Arguments_WaitForThePublisherForever_AndTimeOutOnlyOnIo()
    {
        var input = new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Listen, Port = 1935, Path = "live/estudio1" };
        Assert.Equal(new[] { "-listen", "1", "-timeout", "-1", "-rw_timeout", "10000000", "-i", "rtmp://0.0.0.0:1935/live/estudio1" },
            NetworkStreamReceiver.InputArgumentsFor(input));
    }

    [Fact]
    public void RtmpConnect_Arguments_AreLiveWithIoTimeout_AndRtmpsWhenSecure()
    {
        var input = new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = "srv.example", Port = 443, Path = "live/clave?token=1", Secure = true };
        Assert.Equal(new[] { "-rtmp_live", "live", "-rw_timeout", "10000000", "-i", "rtmps://srv.example:443/live/clave?token=1" },
            NetworkStreamReceiver.InputArgumentsFor(input));
    }

    [Fact]
    public void ThePassphraseTravelsAsItsOwnArgument_SoSpecialCharactersNeedNoEscaping()
    {
        var input = new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000, Passphrase = "cl&ve=con#raros%20" };
        var args = NetworkStreamReceiver.InputArgumentsFor(input).ToList();
        Assert.Equal("cl&ve=con#raros%20", args[args.IndexOf("-passphrase") + 1]);
        Assert.Equal("srt://0.0.0.0:9000", args[^1]); // y la URL queda limpia
    }

    [Fact]
    public void TheReceiver_CopiesTheStreamToTheRelay_WithoutReencoding()
    {
        var input = new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000 };
        var args = NetworkStreamReceiver.ArgumentsFor(input, 45000).ToList();

        Assert.Equal(new[] { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" }, args.Take(5)); // el supervisor lee el progreso por stdout
        Assert.Equal(new[] { "-mode", "listener" }, args.Skip(5).Take(2));
        Assert.Equal("srt://0.0.0.0:9000", args[args.IndexOf("-i") + 1]);
        int copy = args.IndexOf("-c");
        Assert.Equal("copy", args[copy + 1]);              // sin recodificar: el preset lo aplica el proceso del canal
        Assert.Equal("0:v:0", args[args.IndexOf("-map") + 1]);
        Assert.Contains("0:a:0?", args);                   // el audio es opcional: un flujo solo-vídeo no aborta
        Assert.Equal("mpegts", args[args.IndexOf("-f") + 1]);
        Assert.Equal("1", args[args.IndexOf("-flush_packets") + 1]);
        Assert.Equal("50000", args[args.IndexOf("-max_interleave_delta") + 1]); // sin retener el audio 10 s cuando trae otro reloj
        Assert.Contains("-copyts", args); // marcas tal cual: el relé es el único que realinea relojes (SRT y RTMP igual)
        Assert.Equal("tcp://127.0.0.1:45000?tcp_nodelay=1", args[^1]);
        Assert.DoesNotContain("-c:v", args);
        Assert.DoesNotContain("-y", args);
    }

    // --- Señal: lo que la fuente publica a partir del receptor ---

    [Fact]
    public async Task Open_StartsWithoutSignal_AndLocksWhenTheReceiverSeesTheSender()
    {
        var (source, fake, created) = WithFake(new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000 });
        int changes = 0;
        source.SignalChanged += (_, _) => changes++;

        await source.OpenAsync();
        Assert.True(fake.Started);
        Assert.Equal(1, created());
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Equal("Esperando al emisor", source.CurrentSignal.FormatLabel);
        Assert.True(source.WaitsForPeer);
        Assert.True(source.RestartsAfterEndOfStream);
        Assert.True(((ICaptureSource)source).SelfReportsRecovery); // el motor NO sondea: sondear abriría un ffmpeg contra el relé
        Assert.Equal(2, source.AudioChannelCount);
        Assert.Equal(1, changes);

        // Sin emisor no hay pipeline que construir: el motor entra en espera (como con NDI sin receptor).
        var ex = Assert.Throws<InvalidOperationException>(() => source.BuildInputArguments());
        Assert.Contains("aún no hay emisor conectado", ex.Message);

        fake.Connect(Hd25, hasAudio: true);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        Assert.Equal("1920×1080 · 25p", source.CurrentSignal.FormatLabel);
        Assert.Equal(new Resolution(1920, 1080), source.CurrentSignal.Resolution);
        Assert.Equal(new FrameRate(25, 1), source.CurrentSignal.FrameRate);
        Assert.True(source.CurrentSignal.HasAudio);
        Assert.Equal(2, changes);

        // El proceso del canal lee el TS del relé del receptor, no del emisor.
        Assert.Equal(new[] { "-f", "mpegts", "-analyzeduration", "2000000", "-probesize", "5000000", "-i", "tcp://127.0.0.1:43210" },
            source.BuildInputArguments());

        fake.Lose(); // el emisor cerró: el receptor se relanza y vuelve a esperar
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Equal("Esperando al emisor", source.CurrentSignal.FormatLabel);
        Assert.Null(source.CurrentSignal.Resolution);
        Assert.Equal(3, changes);
        fake.Lose(); // repetido (reintentos del receptor): sin ruido
        Assert.Equal(3, changes);

        // Un flujo que FFmpeg no describe del todo (sin tamaño/tasa) y sin audio: SEÑAL OK sin etiqueta y sin mapeo de audio.
        fake.Connect(video: null, hasAudio: false);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        Assert.Null(source.CurrentSignal.FormatLabel);
        Assert.False(source.CurrentSignal.HasAudio);
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task Open_IsIdempotent_AndClose_StopsTheReceiver()
    {
        var (source, fake, created) = WithFake(new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000 });

        await source.OpenAsync();
        await source.OpenAsync(); // el bucle de espera del motor reabre cada 5 s: NO debe reiniciar el receptor (cerraría el puerto)
        Assert.Equal(1, created());
        Assert.True(source.IsOpen);

        fake.Connect(Hd25, hasAudio: true);
        await source.CloseAsync();
        Assert.True(fake.Disposed);
        Assert.False(source.IsOpen);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Throws<InvalidOperationException>(() => source.BuildInputArguments());

        // Un receptor ya cerrado no puede cambiar la señal de la fuente.
        fake.Connect(Hd25, hasAudio: true);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);

        await source.OpenAsync(); // reabrir crea un receptor nuevo
        Assert.Equal(2, created());
        await source.DisposeAsync();
        Assert.False(source.IsOpen);
    }

    [Fact]
    public async Task TheEngineReports_AreIgnored_TheReceiverIsTheTruth()
    {
        var (source, fake, _) = WithFake(new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000 });
        await source.OpenAsync();

        // Lo que el proceso del canal cuente de SU entrada (el relé tcp://127.0.0.1) no habla del emisor.
        source.ReportDeviceOpen(true);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        source.ReportDetectedMode(Hd25);
        Assert.Null(source.CurrentSignal.Resolution);

        fake.Connect(Hd25, hasAudio: true);
        source.ReportDeviceOpen(false);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
    }

    [Fact]
    public async Task ARejectedPassphrase_IsSaidInWords_UntilASenderConnects()
    {
        var (source, fake, _) = WithFake(new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000, Passphrase = "clave-secreta-1" });
        await source.OpenAsync();

        fake.Reject();
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Equal("Contraseña SRT rechazada", source.CurrentSignal.FormatLabel);
        fake.Lose(); // FFmpeg falla con -138 y el supervisor relanza: el motivo se conserva
        Assert.Equal("Contraseña SRT rechazada", source.CurrentSignal.FormatLabel);

        fake.Connect(Hd25, hasAudio: true); // llega uno con la contraseña buena
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        fake.Reject(); // otro con la mala mientras hay flujo: no cambia nada
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        fake.Lose();
        Assert.Equal("Esperando al emisor", source.CurrentSignal.FormatLabel);
    }

    [Fact]
    public async Task ConnectRoles_SayConnecting_WhileTheReceiverCalls()
    {
        var (source, _, _) = WithFake(new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = "srv", Port = 1935, Path = "live/a" });
        await source.OpenAsync();
        Assert.Equal("Conectando…", source.CurrentSignal.FormatLabel);
        Assert.True(source.WaitsForPeer);
        Assert.True(source.RestartsAfterEndOfStream);
    }

    [Fact]
    public async Task AnUnreadableOrInvalidDefinition_FailsToOpen_WithTheReasonInWords()
    {
        int created = 0;
        var garbage = new NetworkStreamCaptureSource(new InputSource { Name = "rota", Type = InputType.SrtListener, Uri = "esto no es una url" }, _ => { created++; return new FakeReceiver(); });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => garbage.OpenAsync());
        Assert.Contains("rota", ex.Message);
        Assert.Contains("No se entiende esa URL", ex.Message);
        Assert.Throws<InvalidOperationException>(() => garbage.BuildInputArguments());
        Assert.Equal(0, created); // ni se intenta arrancar un receptor

        // Una URL que se lee pero no pasa la validación (contraseña SRT corta en los parámetros): el motivo concreto, no «URL rara».
        var shortPass = new InputSource { Name = "p", Type = InputType.SrtListener, Uri = "srt://0.0.0.0:9000" };
        shortPass.Parameters[NetworkInput.PassphraseKey] = "corta";
        var badPass = new NetworkStreamCaptureSource(shortPass, _ => new FakeReceiver());
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => badPass.OpenAsync());
        Assert.Contains("entre 10 y 64", ex2.Message);
    }

    // --- Fábrica y resolver ---

    [Fact]
    public void TheFactory_HandlesTheThreeNetworkTypes_AndTheResolverPicksIt()
    {
        // El localizador solo exige que ffmpeg.exe exista; el receptor no se arranca hasta OpenAsync.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "baioss-net-factory-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), Array.Empty<byte>());
            var factory = new NetworkStreamCaptureSourceFactory(new FfmpegLocator(dir), NullLoggerFactory.Instance);
            Assert.True(factory.CanHandle(InputType.SrtListener));
            Assert.True(factory.CanHandle(InputType.SrtCaller));
            Assert.True(factory.CanHandle(InputType.Rtmp));
            Assert.False(factory.CanHandle(InputType.Udp));
            Assert.False(factory.CanHandle(InputType.File));

            var resolver = new CaptureSourceResolver(new ICaptureSourceFactory[]
            {
                new FileCaptureSourceFactory(), new DecklinkCaptureSourceFactory(), new DirectShowCaptureSourceFactory(), factory,
            });
            var def = new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000 }.ToInputSource(Guid.NewGuid(), "x");
            var source = Assert.IsType<NetworkStreamCaptureSource>(resolver.Create(def));
            Assert.False(source.IsOpen);
            Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
            Assert.IsType<FileCaptureSource>(resolver.Create(new InputSource { Name = "f", Type = InputType.File, Uri = "C:/clip.mp4" }));
            Assert.False(resolver.CanHandle(InputType.Udp));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    // --- Volcado de entrada de FFmpeg (lo que el receptor lee del stderr) ---

    [Fact]
    public void InputDump_DescribesTheInput_WhenTheDumpEnds()
    {
        var dump = new FfmpegInputDump();
        Assert.Null(dump.Feed("ffmpeg stats and -progress period set to 1."));
        Assert.Null(dump.Feed("Input #0, mpegts, from 'srt://0.0.0.0:9720':"));
        Assert.Null(dump.Feed("  Duration: N/A, start: 1.400000, bitrate: N/A"));
        Assert.Null(dump.Feed("  Program 1 "));
        Assert.Null(dump.Feed("  Stream #0:0[0x100]: Video: h264 (Constrained Baseline) ([27][0][0][0] / 0x001B), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 25 fps, 25 tbr, 90k tbn, start 1.421333"));
        Assert.Null(dump.Feed("  Stream #0:1[0x101]: Audio: aac (LC) ([15][0][0][0] / 0x000F), 48000 Hz, mono, fltp, 99 kb/s, start 1.400000"));

        var d = dump.Feed("Stream mapping:");
        Assert.NotNull(d);
        Assert.Equal("srt://0.0.0.0:9720", d!.Url);
        Assert.Equal("640×360 · 25p", d.Video!.Label);
        Assert.True(d.HasAudio);

        // Lo que sigue (mapeo y volcado de la SALIDA, que repite líneas «Stream #0:0: Video») ya no cuenta.
        Assert.Null(dump.Feed("  Stream #0:0 -> #0:0 (copy)"));
        Assert.Null(dump.Feed("Output #0, mpegts, to 'tcp://127.0.0.1:9721?tcp_nodelay=1':"));
        Assert.Null(dump.Feed("  Stream #0:0: Video: h264 (Constrained Baseline), yuv420p(progressive), 1280x720, q=2-31, 25 fps, 25 tbr, 90k tbn"));

        // Al relanzar el proceso, Reset y vuelve a describir.
        dump.Reset();
        Assert.Null(dump.Feed("Input #0, flv, from 'rtmp://0.0.0.0:1935/live/test':"));
        Assert.Null(dump.Feed("  Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080 [SAR 1:1 DAR 16:9], 50 fps, 50 tbr, 1k tbn"));
        var d2 = dump.Feed("Output #0, mpegts, to 'tcp://127.0.0.1:9721?tcp_nodelay=1':"); // también termina en «Output #»
        Assert.Equal("rtmp://0.0.0.0:1935/live/test", d2!.Url);
        Assert.Equal("1920×1080 · 50p", d2.Video!.Label);
        Assert.False(d2.HasAudio); // flujo solo-vídeo: el pipeline no debe mapear audio
    }

    [Fact]
    public void InputDump_IgnoresStreamLinesBeforeAnyInput_AndKeepsAnUndescribedVideo()
    {
        var dump = new FfmpegInputDump();
        Assert.Null(dump.Feed("  Stream #0:0: Video: h264, yuv420p, 640x360, 25 fps")); // sin «Input #» antes: ruido
        Assert.Null(dump.Feed("Stream mapping:"));
        Assert.Null(dump.Feed("Input #0, mpegts, from 'srt://0.0.0.0:9000':"));
        Assert.Null(dump.Feed("  Stream #0:0[0x100]: Video: h264 (Main), none")); // FFmpeg aún no sabe tamaño ni tasa
        Assert.Null(dump.Feed("  Stream #0:1[0x101]: Audio: aac (LC), 48000 Hz, stereo, fltp"));
        var d = dump.Feed("Stream mapping:");
        Assert.NotNull(d);
        Assert.Null(d!.Video);   // SEÑAL OK sin etiqueta de formato, mejor que inventarla
        Assert.True(d.HasAudio);
    }

    // --- Líneas genéricas del log de FFmpeg ---

    [Fact]
    public void InputOpened_MatchesOnlyTheSourceUrl_NotTheSlateGenerators()
    {
        Assert.True(FfmpegLogLines.IsInputOpened("Input #0, mpegts, from 'srt://0.0.0.0:9000':", "srt://0.0.0.0:9000"));
        Assert.True(FfmpegLogLines.IsInputOpened("Input #0, flv, from 'rtmp://0.0.0.0:1935/live/test':", "rtmp://0.0.0.0:1935/live/test"));
        Assert.True(FfmpegLogLines.IsInputOpened("Input #0, decklink, from 'DeckLink Duo (1)':", "DeckLink Duo (1)"));
        Assert.False(FfmpegLogLines.IsInputOpened("Input #0, lavfi, from 'smptebars=size=1920x1080:rate=25':", "srt://0.0.0.0:9000"));
        Assert.False(FfmpegLogLines.IsInputOpened("Input #0, mpegts, from 'srt://0.0.0.0:9001':", "srt://0.0.0.0:9000"));
        Assert.False(FfmpegLogLines.IsInputOpened("  Stream #0:0: Video: h264", "srt://0.0.0.0:9000"));
        Assert.False(FfmpegLogLines.IsInputOpened("Input #0, mpegts, from 'srt://0.0.0.0:9000':", null));
        Assert.Equal("srt://0.0.0.0:9000", FfmpegLogLines.InputUrl("Input #0, mpegts, from 'srt://0.0.0.0:9000':"));
        Assert.Null(FfmpegLogLines.InputUrl("Output #0, mpegts, to 'tcp://127.0.0.1:9721':"));
    }

    [Theory]
    [InlineData("  Stream #0:0[0x100]: Video: h264 (Constrained Baseline) ([27][0][0][0] / 0x001B), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 25 fps, 25 tbr, 90k tbn", 640, 360, 25, 1, false, "640×360 · 25p")]
    [InlineData("  Stream #0:0: Video: h264 (Constrained Baseline), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 25 fps, 25 tbr, 1k tbn, start 0.021000", 640, 360, 25, 1, false, "640×360 · 25p")]
    [InlineData("  Stream #0:1: Video: rawvideo (UYVY / 0x59565955), uyvy422(top first), 1920x1080, 994333 kb/s, 29.97 tbr, 1000k tbn", 1920, 1080, 30000, 1001, true, "1920×1080 · 59.94i")]
    [InlineData("  Stream #0:0: Video: hevc (Main), yuv420p(tv), 1280x720, 59.94 fps, 59.94 tbr, 90k tbn", 1280, 720, 60000, 1001, false, "1280×720 · 59.94p")]
    public void VideoStreamLine_GivesTheRealFormat(string line, int w, int h, int num, int den, bool interlaced, string label)
    {
        var mode = FfmpegLogLines.TryParseVideoStream(line)!;
        Assert.Equal(new Resolution(w, h), mode.Resolution);
        Assert.Equal(new FrameRate(num, den), mode.FrameRate);
        Assert.Equal(interlaced, mode.Interlaced);
        Assert.Equal(label, mode.Label);
    }

    [Theory]
    [InlineData("  Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp, 96 kb/s")]
    [InlineData("Input #0, mpegts, from 'srt://0.0.0.0:9000':")]
    [InlineData("  Stream #0:0: Video: h264, yuv420p")] // sin tamaño ni tasa
    [InlineData("frame=   74 fps= 43 q=-0.0 Lsize=N/A time=00:00:03.00")]
    public void OtherLines_AreNotAVideoFormat(string line) => Assert.Null(FfmpegLogLines.TryParseVideoStream(line));

    // --- Supervisor: las dos opciones de las que dependen el receptor y el proceso del canal ---

    private static string Cmd => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    [Fact]
    public async Task ACleanExit_IsRestarted_OnlyWhenTheOwnerAsksForIt()
    {
        // Un flujo de red que termina (el emisor cerró) sale con 0: sin RestartOnCleanExit el receptor se daría por acabado.
        await using var restarting = new FfmpegProcessSupervisor(Cmd, NullLogger.Instance) { RestartOnCleanExit = true, FinalizeOnStop = false };
        var restarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        restarting.Restarted += (_, n) => restarted.TrySetResult(n);
        await restarting.StartAsync(new[] { "/c", "exit 0" });
        Assert.Same(restarted.Task, await Task.WhenAny(restarted.Task, Task.Delay(TimeSpan.FromSeconds(8))));

        await using var plain = new FfmpegProcessSupervisor(Cmd, NullLogger.Instance) { FinalizeOnStop = false };
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool restartedPlain = false;
        plain.Completed += (_, code) => completed.TrySetResult(code);
        plain.Restarted += (_, _) => restartedPlain = true;
        await plain.StartAsync(new[] { "/c", "exit 0" });
        Assert.Same(completed.Task, await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(8))));
        Assert.Equal(0, await completed.Task);
        Assert.False(restartedPlain);
    }

    [Fact]
    public async Task AProcessThatWaitsWithoutProgress_IsNotKilled_WhenTheSourceWaitsForAPeer()
    {
        // «ping» calla ~7 s sin escribir nada por stdout: es lo que hace FFmpeg mientras espera al emisor.
        var waiting = new[] { "/c", "ping -n 8 127.0.0.1 > NUL" };

        await using var strict = new FfmpegProcessSupervisor(Cmd, NullLogger.Instance)
            { StallTimeout = TimeSpan.FromSeconds(1), FinalizeOnStop = false, RestartInternally = false };
        var crashed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        strict.Crashed += (_, code) => crashed.TrySetResult(code);
        await strict.StartAsync(waiting);
        Assert.Same(crashed.Task, await Task.WhenAny(crashed.Task, Task.Delay(TimeSpan.FromSeconds(6)))); // el vigilante lo mata

        await using var patient = new FfmpegProcessSupervisor(Cmd, NullLogger.Instance)
            { StallTimeout = TimeSpan.FromSeconds(1), FinalizeOnStop = false, RestartInternally = false, IgnoreStallUntilFirstProgress = true };
        var crashedPatient = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        patient.Crashed += (_, code) => crashedPatient.TrySetResult(code);
        await patient.StartAsync(waiting);
        Assert.NotSame(crashedPatient.Task, await Task.WhenAny(crashedPatient.Task, Task.Delay(TimeSpan.FromSeconds(4)))); // sigue vivo
    }
}
