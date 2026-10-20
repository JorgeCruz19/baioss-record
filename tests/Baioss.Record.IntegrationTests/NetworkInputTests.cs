using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
using Xunit.Abstractions;

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
    private readonly ITestOutputHelper _out;
    public NetworkInputTests(ITestOutputHelper output) => _out = output;

    private static bool Available => TestAssets.FfmpegDir is not null;

    // --- Emisor: un ffmpeg que empuja barras + tono por la URL indicada ---

    private sealed class Sender : IDisposable
    {
        private readonly Process _process;

        /// <param name="syncMarks">En vez de barras y tono, un destello blanco de un cuadro y un pitido de 40 ms A LA VEZ cada 2 s (ver
        /// <see cref="FlashToBeepAsync"/>), con las dos entradas a ritmo real y el audio enviado en cuanto sale del codificador, como un
        /// codificador en directo: así lo grabado dice si el audio quedó donde estaba respecto al vídeo.</param>
        public Sender(string ffmpegDir, string url, string format, int seconds, string? passphrase = null, bool listen = false, double audioOffsetSeconds = 0,
            bool syncMarks = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(ffmpegDir, "ffmpeg.exe"),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error" };
            if (syncMarks)
                args.AddRange(new[]
                {
                    "-re", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=25,drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='lt(mod(t,2),0.04)'",
                    "-re", "-f", "lavfi", "-i", "aevalsrc='if(lt(mod(t,2),0.04),0.8*sin(2*PI*1000*t),0)':s=48000",
                    "-ac", "2",
                });
            else
                args.AddRange(new[]
                {
                    "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25",
                    "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                });
            args.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "25",
                "-c:a", "aac", "-b:a", "96k",
            });
            // Cada trama de audio sale en cuanto la da el codificador: ni retenida para entrelazarla por DTS ni agrupada con otras en un
            // PES (lo que el muxer MPEG-TS hace por defecto: 8 tramas a 135 kb/s, hasta 350 ms de silencio), que la haría llegar tarde
            // respecto al vídeo. Como el receptor (ver NetworkStreamReceiver.ArgumentsFor).
            if (syncMarks)
            {
                args.Add("-max_interleave_delta"); args.Add("1");
                if (format == "mpegts") args.AddRange(new[] { "-pes_payload_size", "0", "-muxdelay", "0" });
            }
            // -t compara las marcas de tiempo con la duración: con el audio desplazado 100 s lo descartaría entero; ese emisor
            // se para matándolo (Dispose), como un emisor real que no termina solo.
            if (audioOffsetSeconds == 0) { args.Add("-t"); args.Add(seconds.ToString()); }
            // Emisor con RELOJES DISTINTOS para audio y vídeo (como algunos servidores/codificadores reales): el audio sale con
            // sus marcas de tiempo desplazadas (BSF setts a la SALIDA: con -itsoffset en la entrada, -re retendría el audio
            // 100 s) y viaja entrelazado en tiempo real con el vídeo (sin retenerlo por DTS: -max_interleave_delta corto).
            if (audioOffsetSeconds != 0)
            {
                args.Add("-bsf:a"); args.Add($"setts=ts=TS+{audioOffsetSeconds.ToString(CultureInfo.InvariantCulture)}/TB");
                if (!syncMarks) { args.Add("-max_interleave_delta"); args.Add("100000"); }
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

    /// <summary>
    /// Cuánto va el pitido respecto al destello en lo grabado (mediana de los pares, en segundos; positivo = el audio suena después
    /// de la imagen) y cuántos pares hubo. Los destellos, por el brillo medio de cada cuadro (signalstats); los pitidos, por el final
    /// de cada silencio (silencedetect). Los dos salen del emisor a la vez (ver <see cref="Sender"/> con syncMarks).
    /// </summary>
    private static async Task<(double Offset, int Pairs)> FlashToBeepAsync(string ffmpeg, string file)
    {
        var (_, video) = await RecordedFileCheck.RunWithLogAsync(ffmpeg, "-hide_banner", "-nostats", "-i", file, "-map", "0:v:0",
            "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-");
        // metadata=print escribe, por cuadro, «frame:N pts:P pts_time:T» y después «lavfi.signalstats.YAVG=V».
        var flashes = new List<double>();
        double? at = null;
        foreach (var line in video.Split('\n'))
        {
            if (Regex.Match(line, @"pts_time:([\d.]+)") is { Success: true } time) { at = double.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            if (Regex.Match(line, @"lavfi\.signalstats\.YAVG=([\d.]+)") is { Success: true } y && at is { } t
                && double.Parse(y.Groups[1].Value, CultureInfo.InvariantCulture) > 128)
                flashes.Add(t);
        }
        var (_, audio) = await RecordedFileCheck.RunWithLogAsync(ffmpeg, "-hide_banner", "-nostats", "-i", file, "-map", "0:a:0",
            "-af", "silencedetect=noise=-30dB:d=0.1", "-f", "null", "-");
        var beeps = Regex.Matches(audio, @"silence_end: ([\d.]+)").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        var offsets = flashes
            .Select(f => beeps.Select(b => b - f).Where(d => Math.Abs(d) < 0.9).OrderBy(Math.Abs).Cast<double?>().FirstOrDefault())
            .OfType<double>().OrderBy(d => d).ToList();
        return (offsets.Count == 0 ? double.NaN : offsets[offsets.Count / 2], offsets.Count);
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
    public async Task SrtConnect_FromAPastedUrl_WithAHashInThePassphrase_Locks()
    {
        // Caso real (2026-09-24): ffplay abría «srt://…?passphrase=Televicentro2023#&mode=caller» y el Record no, porque al
        // leer la URL la contraseña se cortaba en el «#». El emisor escucha con esa contraseña y el Record llama con la
        // definición LEÍDA DE LA URL, como al pegarla en Fuentes de red.
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("srt-hash");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeUdpPort();
            using var sender = new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=listener", "mpegts", 60, passphrase: "Televicentro2023#");
            await Task.Delay(1500);

            Assert.True(NetworkInput.TryParseUrl($"srt://127.0.0.1:{port}?passphrase=Televicentro2023#&mode=caller", out var input, out _));
            await using var source = await OpenAsync(locator, input!);
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó con la contraseña leída de la URL.");
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

    /// <summary>
    /// La SINCRONÍA, no solo el arranque: un emisor RTMP manda un destello y un pitido a la vez cada 2 s y lo grabado debe tenerlos
    /// juntos. Con relojes coherentes mandan las marcas de tiempo; con el audio en otro reloj (100 s «por delante», como el servidor
    /// RTMP real con 3056 s), el relé lo realinea por ORDEN DE LLEGADA, así que importa cuándo le llega cada trama: el receptor
    /// juntaba varias tramas AAC en cada PES del MPEG-TS (lo que hace el muxer por defecto: 8 a 135 kb/s, hasta 350 ms de silencio)
    /// y retenía el audio 50 ms para entrelazarlo, y el relé, que mide por el primer PTS de cada PES, colocaba el audio tarde: 0,27 s
    /// en el servidor real (medido el 2026-10-19) y 0,47 s con este emisor (ver <see cref="NetworkStreamReceiver.ArgumentsFor"/>).
    /// </summary>
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task RtmpWithSeparateOrCoherentClocks_TheRecordingKeepsTheAudioInSyncWithTheVideo(double audioOffsetSeconds)
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("rtmp-sync");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeTcpPort();
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Listen, Port = port, Path = "live/test" });
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await Task.Delay(1500);

            using var sender = new Sender(TestAssets.FfmpegDir!, $"rtmp://127.0.0.1:{port}/live/test", "flv", 60, audioOffsetSeconds: audioOffsetSeconds, syncMarks: true);
            await WaitForAsync(() => source.CurrentSignal.State == SignalState.Locked, Lock, "La fuente no bloqueó al publicar el emisor RTMP.");
            await Task.Delay(TimeSpan.FromSeconds(5)); // con relojes distintos, pasado el asentamiento del relé (2,5–4 s)
            var file = await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(9));

            var (offset, pairs) = await FlashToBeepAsync(locator.FfmpegPath, file);
            _out.WriteLine($"Relojes {(audioOffsetSeconds == 0 ? "coherentes" : $"distintos (audio +{audioOffsetSeconds} s)")}: el pitido suena {offset * 1000:+0;-0} ms respecto al destello ({pairs} pares).");
            Assert.True(pairs >= 4, $"Se esperaban al menos 4 pares destello-pitido en lo grabado; hay {pairs}.");
            // Con relojes coherentes, 0 ms. Con relojes distintos el relé alinea por llegada, y la llegada no es exacta: el emisor
            // entrega cada trama de audio ~65 ms después de su vídeo (el codificador AAC necesita la trama siguiente, y su muxer la
            // retiene hasta que llega otra) y el receptor otras dos tramas (~42 ms): +117 ms medidos. Antes, +467 ms.
            Assert.True(Math.Abs(offset) <= 0.15, $"El pitido suena {offset * 1000:+0;-0} ms respecto al destello ({pairs} pares).");
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
