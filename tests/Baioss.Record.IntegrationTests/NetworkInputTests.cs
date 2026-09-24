using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Entradas de red de verdad, en loopback, con el FFmpeg empaquetado: un segundo ffmpeg hace de EMISOR (barras + tono,
/// H.264/AAC) por SRT o RTMP y el motor del canal recibe, previsualiza y GRABA como con cualquier otra entrada. Cubre
/// los cuatro modos (SRT escucha/llamada, RTMP servidor/cliente), la señal (sin señal → SEÑAL OK → sin señal), la
/// contraseña SRT equivocada y la vuelta del emisor tanto en preview como a mitad de una grabación (pieza nueva).
/// Grabar y Detener reemplazan el proceso del canal: con el receptor permanente el emisor NO se entera (antes cada
/// Grabar cerraba la conexión y un emisor sin reconexión automática, como estos ffmpeg, no volvía: la grabación salía vacía).
/// </summary>
public sealed class NetworkInputTests
{
    private static readonly TimeSpan Lock = TimeSpan.FromSeconds(30);

    private static bool Available => TestAssets.FfmpegDir is not null;

    // --- Emisor: un ffmpeg que empuja barras + tono por la URL indicada ---

    private sealed class Sender : IDisposable
    {
        private readonly Process _process;

        public Sender(string ffmpegDir, string url, string format, int seconds, string? passphrase = null, bool listen = false, double audioOffsetSeconds = 0)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(ffmpegDir, "ffmpeg.exe"),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error",
                "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25",
            };
            args.AddRange(new[]
            {
                "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "25",
                "-c:a", "aac", "-b:a", "96k",
            });
            // -t compara las marcas de tiempo con la duración: con el audio desplazado 100 s lo descartaría entero; ese emisor
            // se para matándolo (Dispose), como un emisor real que no termina solo.
            if (audioOffsetSeconds == 0) { args.Add("-t"); args.Add(seconds.ToString()); }
            // Emisor con RELOJES DISTINTOS para audio y vídeo (como algunos servidores/codificadores reales): el audio sale con
            // sus marcas de tiempo desplazadas (BSF setts a la SALIDA: con -itsoffset en la entrada, -re retendría el audio
            // 100 s) y viaja entrelazado en tiempo real con el vídeo (sin retenerlo por DTS: -max_interleave_delta corto).
            if (audioOffsetSeconds != 0)
            {
                args.Add("-bsf:a"); args.Add($"setts=ts=TS+{audioOffsetSeconds.ToString(CultureInfo.InvariantCulture)}/TB");
                args.Add("-max_interleave_delta"); args.Add("100000");
            } // 0 = esperar a todas las pistas: NUNCA escribiría el audio
            if (passphrase is not null) { args.Add("-passphrase"); args.Add(passphrase); }
            args.Add("-f"); args.Add(format);
            if (listen) { args.Add("-listen"); args.Add("1"); }
            args.Add(url);
            foreach (var a in args) psi.ArgumentList.Add(a);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo lanzar el emisor.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public bool HasExited => _process.HasExited;

        public void Dispose()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* ya salió */ }
            _process.Dispose();
        }
    }

    private static int FreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static RecordingProfile Profile() => new()
    {
        Name = "net", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(3), GopSize = 25,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    /// <summary>La fuente REAL (receptor permanente FFmpeg + relé), como la crea la app; el llamador la dispone.</summary>
    private static async Task<NetworkStreamCaptureSource> OpenAsync(FfmpegLocator locator, NetworkInput input)
    {
        var factory = new NetworkStreamCaptureSourceFactory(locator, NullLoggerFactory.Instance);
        var source = (NetworkStreamCaptureSource)factory.Create(input.ToInputSource(Guid.NewGuid(), "prueba de red"));
        await source.OpenAsync();
        return source;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(250);
        }
    }

    private static async Task<string> ProbeCodecAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output.Trim();
    }

    /// <summary>Instante de inicio (s) de la pista de vídeo y de la de audio del archivo, según ffprobe.</summary>
    private static async Task<(double Video, double? Audio)> ProbeStartTimesAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_type,start_time", "-of", "csv=p=0", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        double video = double.NaN; double? audio = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)) continue;
            if (parts[0] == "video") video = start; else if (parts[0] == "audio") audio = start;
        }
        return (video, audio);
    }

    private static async Task<string> RecordAndVerifyAsync(FfmpegChannelEngine engine, FfmpegLocator locator, RecordingProfile profile, TimeSpan duration)
    {
        await engine.StartRecordingAsync(Guid.NewGuid(), profile);
        await Task.Delay(duration);
        await engine.StopRecordingAsync();
        var file = engine.LastOutputFile;
        Assert.True(file is not null && File.Exists(file), $"No se generó el archivo: {file}");
        Assert.True(new FileInfo(file!).Length > 0, "El archivo de grabación está vacío.");
        Assert.Equal("h264", await ProbeCodecAsync(locator.FfprobePath, file!));
        return file!;
    }

    private static string OutputRoot(string tag) => Path.Combine(Path.GetTempPath(), $"baioss-net-{tag}-{Guid.NewGuid():N}");

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    // --- Los cuatro modos ---

    [SkippableFact]
    public async Task SrtListen_RecordsWhatTheSenderPushes_AndTheSignalFollowsTheSender()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("srt-listen");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeUdpPort();
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = port });
            Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State); // nadie ha llamado todavía

            int frames = 0;
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await Task.Delay(1500); // FFmpeg ya escucha; sin emisor sigue sin señal y nadie lo mata
            Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);

            using (var sender = new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller&latency=120000", "mpegts", 60))
            {
                await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó al llegar el emisor SRT.");
                await WaitForAsync(() => Volatile.Read(ref frames) >= 3, Lock, "El preview no entregó frames del flujo SRT.");
                Assert.Equal("640×360 · 25p", source.CurrentSignal.FormatLabel); // el formato real, leído del log de FFmpeg

                await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(3));
            }

            // El emisor se fue: FFmpeg sale limpiamente, el supervisor relanza y la fuente vuelve a «sin señal».
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.NoSignal, Lock, "La fuente no marcó SIN SEÑAL al irse el emisor.");

            // Y vuelve: el nuevo FFmpeg estaba escuchando otra vez.
            using (new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller&latency=120000", "mpegts", 30))
                await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no volvió a bloquear al regresar el emisor.");
        }
        finally { Cleanup(outputRoot); }
    }

    [SkippableFact]
    public async Task SrtConnect_PullsFromASenderThatListens()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("srt-connect");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeUdpPort();
            using var sender = new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=listener", "mpegts", 60);
            await Task.Delay(1500);

            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Connect, Host = "127.0.0.1", Port = port, StreamId = "record" });
            Assert.Equal("Conectando…", source.CurrentSignal.FormatLabel);
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó al llamar al emisor SRT.");
            await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(3));
        }
        finally { Cleanup(outputRoot); }
    }

    [SkippableFact]
    public async Task RtmpListen_RecordsAPublishedStream()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("rtmp-listen");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeTcpPort();
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Listen, Port = port, Path = "live/test" });
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await Task.Delay(1500);
            Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);

            using var sender = new Sender(TestAssets.FfmpegDir!, $"rtmp://127.0.0.1:{port}/live/test", "flv", 60);
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó al publicar el emisor RTMP.");
            Assert.Equal("640×360 · 25p", source.CurrentSignal.FormatLabel);
            await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(3));
        }
        finally { Cleanup(outputRoot); }
    }

    [SkippableFact]
    public async Task RtmpConnect_PullsFromAServer()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("rtmp-connect");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeTcpPort();
            // El «servidor»: un ffmpeg que sirve el flujo al primer cliente que lo pida (-listen 1 en la salida).
            using var server = new Sender(TestAssets.FfmpegDir!, $"rtmp://127.0.0.1:{port}/live/test", "flv", 60, listen: true);
            await Task.Delay(1500);

            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = "127.0.0.1", Port = port, Path = "live/test" });
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó al tirar del servidor RTMP.");
            await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(3));
        }
        finally { Cleanup(outputRoot); }
    }

    [SkippableFact]
    public async Task RtmpConnect_WithoutApplicationOrKey_PullsFromADeviceThatServesAtTheRoot()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("rtmp-connect-root");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeTcpPort();
            // Hay dispositivos (codificadores/decodificadores) cuya URL es solo rtmp://ip:puerto, sin aplicación ni clave.
            using var server = new Sender(TestAssets.FfmpegDir!, $"rtmp://127.0.0.1:{port}", "flv", 60, listen: true);
            await Task.Delay(1500);

            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = "127.0.0.1", Port = port, Path = "" });
            Assert.Equal($"rtmp://127.0.0.1:{port}", source.Input!.Url); // tal cual la da el dispositivo
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó al tirar de un servidor RTMP sin aplicación/clave.");
            await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(3));
        }
        finally { Cleanup(outputRoot); }
    }

    [SkippableFact]
    public async Task RtmpListen_WithoutApplicationOrKey_AcceptsASenderThatPublishesToTheBareAddress()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("rtmp-listen-root");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeTcpPort();
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Listen, Port = port, Path = "" });
            Assert.Equal($"rtmp://0.0.0.0:{port}/", source.Input!.Url); // la barra final = «clave vacía» para el servidor de FFmpeg
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await Task.Delay(1500);

            // El emisor publica a rtmp://ip:puerto sin más (como los codificadores que no admiten aplicación/clave).
            using var sender = new Sender(TestAssets.FfmpegDir!, $"rtmp://127.0.0.1:{port}", "flv", 60);
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó al publicar el emisor sin aplicación/clave.");
            Assert.Equal("640×360 · 25p", source.CurrentSignal.FormatLabel);
            await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(3));
        }
        finally { Cleanup(outputRoot); }
    }

    [SkippableFact]
    public async Task WhenTheSenderHasSeparateAudioAndVideoClocks_TheAudioIsRealignedToTheVideo()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("srt-clock-skew");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeUdpPort();
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = port });
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");

            // El audio llega con sus marcas de tiempo 100 s «por delante» del vídeo (dos relojes): sin corrección, el MP4
            // saldría con el audio 100 s desplazado (medido en un servidor RTMP real, con 3,4 h de desfase).
            using var sender = new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller&latency=120000", "mpegts", 60, audioOffsetSeconds: 100);
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó.");
            var file = await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(4));

            var (video, audio) = await ProbeStartTimesAsync(locator.FfprobePath, file);
            Assert.NotNull(audio); // hay pista de audio con datos
            Assert.True(Math.Abs(audio!.Value - video) < 0.5, $"El audio debía empezar junto al vídeo; vídeo {video:0.000} s, audio {audio:0.000} s.");
        }
        finally { Cleanup(outputRoot); }
    }

    // --- Casos de fallo y recuperación ---

    [SkippableFact]
    public async Task SrtListen_WithTheWrongPassphrase_NeverLocks_AndKeepsListening()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("srt-badpass");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeUdpPort();
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = port, Passphrase = "clave-secreta-1" });
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await Task.Delay(1500);

            using (new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller", "mpegts", 8, passphrase: "otra-clave-9999"))
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    Assert.NotEqual(SignalState.Locked, source.CurrentSignal.State); // la conexión se rechaza: nunca hay flujo
                    await Task.Delay(250);
                }
            }

            // Con la contraseña buena, el mismo canal (relanzado por el supervisor tras el rechazo) sí engancha.
            using (new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller", "mpegts", 30, passphrase: "clave-secreta-1"))
                await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(45), "Con la contraseña correcta la fuente debía bloquear.");
        }
        finally { Cleanup(outputRoot); }
    }

    [SkippableFact]
    public async Task WhenTheSenderDropsMidRecording_TheRecordingContinuesInANewPiece_WhenItReturns()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("srt-drop");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeUdpPort();
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = port });
            var segments = new List<Segment>();
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            engine.SegmentClosed += (_, s) => { lock (segments) segments.Add(s); };
            await engine.StartPreviewAsync(source, Profile(), "NET");

            using (new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller&latency=120000", "mpegts", 60))
            {
                await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó.");
                await engine.StartRecordingAsync(Guid.NewGuid(), Profile());
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            var firstPiece = engine.LastOutputFile;
            // El emisor se fue a mitad de la grabación: el relé cierra, FFmpeg finaliza la pieza y el motor vuelve a esperar.
            await WaitForAsync(() => { lock (segments) return segments.Count >= 1; }, Lock, "No se cerró la primera pieza al irse el emisor.");

            using (new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller&latency=120000", "mpegts", 60))
            {
                await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(45), "La fuente no volvió a bloquear en la grabación.");
                // La recuperación reintenta con backoff (1→2→4→8… s): espera a que la pieza nueva esté de verdad en marcha.
                await WaitForAsync(() => engine.LastOutputFile is { } f && f != firstPiece, TimeSpan.FromSeconds(45), "No arrancó la pieza nueva al volver el emisor.");
                await Task.Delay(TimeSpan.FromSeconds(3));
                await engine.StopRecordingAsync();
            }

            Segment[] closed;
            lock (segments) closed = segments.ToArray();
            Assert.True(closed.Length >= 2, $"Se esperaban al menos dos piezas (antes y después de la caída); hay {closed.Length}.");
            foreach (var s in closed)
            {
                Assert.True(File.Exists(s.FilePath), $"Falta la pieza {s.FilePath}");
                Assert.Equal("h264", await ProbeCodecAsync(locator.FfprobePath, s.FilePath));
            }
            Assert.Equal(closed.Length, closed.Select(s => s.FilePath).Distinct().Count()); // piezas distintas, ninguna truncada
        }
        finally { Cleanup(outputRoot); }
    }
}
