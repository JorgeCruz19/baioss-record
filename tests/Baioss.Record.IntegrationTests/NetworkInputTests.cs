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
/// contraseña SRT equivocada, una pista de audio que no trae datos y la vuelta del emisor tanto en preview como a mitad de
/// una grabación (pieza nueva).
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
        /// <param name="firstKeyframeLost">El emisor se come su primer fotograma clave (y con él los parámetros del vídeo): el
        /// receptor recibe frames que no puede describir hasta el siguiente clave, un GOP después, como al entrar a un directo justo
        /// detrás de uno. GOP fijo de <paramref name="gopFrames"/> (sin claves de más por cambio de escena).</param>
        /// <param name="audioLateSeconds">El audio está en la PMT desde el principio, pero sus primeros paquetes no llegan hasta pasados
        /// estos segundos.</param>
        /// <param name="audioNeverSent">El audio está en la PMT, pero no manda NI UN paquete (el codificador lo genera y se tira entero).</param>
        public Sender(string ffmpegDir, string url, string format, int seconds, string? passphrase = null, bool listen = false, double audioOffsetSeconds = 0,
            bool syncMarks = false, string videoCodec = "libx264", int gopFrames = 25, bool firstKeyframeLost = false, double audioLateSeconds = 0,
            bool withAudio = true, bool audioNeverSent = false)
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
            {
                args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25" });
                if (withAudio) args.AddRange(new[] { "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000" });
            }
            args.AddRange(new[] { "-c:v", videoCodec, "-preset", "ultrafast", "-tune", "zerolatency", "-g", gopFrames.ToString(CultureInfo.InvariantCulture) });
            if (videoCodec == "libx265") args.AddRange(new[] { "-x265-params", firstKeyframeLost ? "log-level=error:scenecut=0" : "log-level=error" });
            else if (firstKeyframeLost) args.AddRange(new[] { "-sc_threshold", "0" });
            // El primer paquete de vídeo (n = 0) es el fotograma clave con los parámetros (SPS/PPS, y VPS en HEVC).
            if (firstKeyframeLost) { args.Add("-bsf:v"); args.Add("noise=drop=not(n)"); }
            args.AddRange(withAudio ? new[] { "-c:a", "aac", "-b:a", "96k" } : new[] { "-an" });
            if (audioLateSeconds > 0) { args.Add("-bsf:a"); args.Add($"noise=drop=lt(pts*tb\\,{audioLateSeconds.ToString(CultureInfo.InvariantCulture)})"); }
            // Sin paquetes de audio, el muxer retendría el vídeo 10 s (-max_interleave_delta por defecto) esperándolos; un codificador
            // en directo lo manda al momento.
            if (audioNeverSent) args.AddRange(new[] { "-bsf:a", "noise=drop=1", "-max_interleave_delta", "100000" });
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

    // --- Cuánto tarda SEÑAL OK ---

    /// <summary>El audio del emisor de <see cref="SrtListen_LocksAsSoonAsTheStreamIsDescribed_AlsoAfterACut"/>.</summary>
    public enum SenderAudio { Normal, ThreeSecondsLate, None }

    /// <summary>Instantes (reloj de la prueba) de SEÑAL OK y del primer cuadro de preview posterior, por conexión del emisor.</summary>
    private sealed class LockMarks(Stopwatch clock)
    {
        private readonly object _sync = new();
        private double? _locked, _frame;
        public double Now => clock.Elapsed.TotalSeconds;
        public void Signal(SignalState state) { lock (_sync) if (state == SignalState.Locked) _locked ??= clock.Elapsed.TotalSeconds; }
        public void Frame() { lock (_sync) if (_locked is not null) _frame ??= clock.Elapsed.TotalSeconds; }
        public double? Locked { get { lock (_sync) return _locked; } }
        public double? FirstFrame { get { lock (_sync) return _frame; } }
        public void Reset() { lock (_sync) { _locked = null; _frame = null; } }
    }

    /// <summary>
    /// SEÑAL OK de una SRT en cuanto el receptor conoce el flujo entero —y otra vez igual de rápido cuando el emisor vuelve tras un
    /// corte—, con el formato completo. El emisor pierde su primer fotograma clave, como al entrar a un directo justo detrás de uno: el
    /// peor momento, hay que esperar un GOP entero al siguiente. Con el análisis por defecto de FFmpeg, un MPEG-TS lo agotaba siempre
    /// (ver <see cref="NetworkStreamReceiver.SrtAnalysisArguments"/>); medido el 2026-10-20 con HEVC 1080p: GOP 2 s, 5,7 s; GOP 8 s,
    /// 6,8 s y SIN resolución; audio 3 s tarde, 6,7 s; solo vídeo, 6,8 s. La SRT real del usuario (HEVC, GOP 2 s): ~6,2 s, y ~7,1 s
    /// tras cada corte. Esta prueba con los argumentos de antes: GOP 2 s, 5,3–5,4 s; GOP 8 s, 7,2 s sin formato; ahora 2,2–2,35 s y 8,0 s.
    /// </summary>
    [SkippableTheory]
    [InlineData("libx265", 2, SenderAudio.Normal)]
    [InlineData("libx264", 2, SenderAudio.Normal)]
    [InlineData("libx265", 8, SenderAudio.Normal)]
    [InlineData("libx265", 2, SenderAudio.ThreeSecondsLate)]
    [InlineData("libx265", 2, SenderAudio.None)]
    public async Task SrtListen_LocksAsSoonAsTheStreamIsDescribed_AlsoAfterACut(string codec, int gopSeconds, SenderAudio audio)
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("srt-lock-time");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeUdpPort();
            var marks = new LockMarks(Stopwatch.StartNew());
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = port });
            source.SignalChanged += (_, s) => marks.Signal(s.State);
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            engine.FrameReady += (_, _) => marks.Frame();
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await Task.Delay(1500); // el receptor ya escucha

            // Lo que hay que esperar sí o sí: el siguiente fotograma clave y, si llega tarde, el audio (está en la PMT: hay que describirlo).
            double wait = Math.Max(gopSeconds, audio == SenderAudio.ThreeSecondsLate ? 3 : 0);
            for (int connection = 1; connection <= 2; connection++)
            {
                marks.Reset();
                double startedAt = marks.Now;
                using var sender = new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller&latency=120000", "mpegts", gopSeconds + 6,
                    videoCodec: codec, gopFrames: gopSeconds * 25, firstKeyframeLost: true,
                    audioLateSeconds: audio == SenderAudio.ThreeSecondsLate ? 3 : 0, withAudio: audio != SenderAudio.None);
                await WaitForAsync(() => marks.Locked is not null, Lock, $"Conexión {connection}: la fuente no dio SEÑAL OK.");
                var signal = source.CurrentSignal;
                double locked = marks.Locked!.Value - startedAt;
                _out.WriteLine($"{codec}, GOP {gopSeconds} s, audio {audio}, conexión {connection}: SEÑAL OK a los {locked:0.00} s ({signal.FormatLabel}" +
                               $"{(signal.HasAudio ? ", con audio" : ", sin audio")}).");

                Assert.Equal("640×360 · 25p", signal.FormatLabel); // el formato entero: un análisis cortado deja el vídeo sin tamaño
                Assert.Equal(new Resolution(640, 360), signal.Resolution);
                Assert.Equal(new FrameRate(25, 1), signal.FrameRate);
                Assert.Equal(audio != SenderAudio.None, signal.HasAudio);
                Assert.True(locked >= wait - 0.5,
                    $"SEÑAL OK a los {locked:0.00} s: el emisor debía hacer esperar al receptor {wait} s (¿no perdió su primer fotograma clave?).");
                Assert.True(locked <= wait + 2.0, $"Conexión {connection}: SEÑAL OK a los {locked:0.00} s; con todo descrito a los {wait} s debía llegar en ≤ {wait + 2:0} s.");

                // Y el canal pinta: con audio, el primer proceso recibe también el audio que el receptor leyó antes del fotograma clave y
                // su análisis (2 s) se completa al momento; sin audio espera 2 s de vídeo en directo.
                await WaitForAsync(() => marks.FirstFrame is not null, Lock, $"Conexión {connection}: SEÑAL OK sin cuadros de preview.");
                _out.WriteLine($"  primer cuadro de preview a los {marks.FirstFrame!.Value - startedAt:0.00} s.");

                if (connection == 2) break;
                // El corte: el emisor termina su flujo; la fuente vuelve a SIN SEÑAL y el receptor, relanzado, escucha otra vez.
                await WaitForAsync(() => sender.HasExited, Lock, "El emisor no terminó su flujo.");
                await WaitForAsync(() => source.CurrentSignal.State == SignalState.NoSignal, Lock, "La fuente no marcó SIN SEÑAL al irse el emisor.");
                await Task.Delay(1000);
            }
        }
        finally { Cleanup(outputRoot); }
    }

    // --- Casos de fallo y recuperación ---

    /// <summary>
    /// Un emisor SRT cuya PMT anuncia una pista de audio que nunca trae paquetes (visto al medir el arranque de las SRT, 2026-10-20). El
    /// análisis de FFmpeg acaba con esa pista sin frecuencia ni canales y, al copiarla, el muxer del receptor no podía escribir la
    /// cabecera («sample rate not set»): el receptor moría justo después de volcar la entrada —la fuente ya en SEÑAL OK y con audio—,
    /// el supervisor lo relanzaba y la señal parpadeaba sin que el canal pintara nunca. Ahora el receptor copia la primera pista de
    /// audio solo si FFmpeg la describió (ver <see cref="NetworkStreamReceiver.AudioMapArguments"/>): SEÑAL OK sin audio cuando el
    /// análisis agota su tope (12 s de vídeo), sin parpadeo, y el canal pinta y graba el vídeo.
    /// </summary>
    [SkippableFact]
    public async Task SrtListen_WithAnAudioTrackThatNeverCarriesData_LocksWithoutAudio_AndPaintsAndRecordsTheVideo()
    {
        Skip.IfNot(Available, "FFmpeg no disponible en tools/.");
        var outputRoot = OutputRoot("srt-mute-audio");
        try
        {
            var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
            int port = FreeUdpPort();
            var clock = Stopwatch.StartNew();
            await using var source = await OpenAsync(locator, new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = port });
            // Cada cambio de señal, con su instante: con el fallo, SEÑAL OK duraba milisegundos (el receptor moría justo después) y
            // mirar CurrentSignal cada 250 ms no llegaba ni a verla.
            var signals = new List<(double At, SignalInfo Signal)>();
            source.SignalChanged += (_, s) => { lock (signals) signals.Add((clock.Elapsed.TotalSeconds, s)); };
            List<(double At, SignalInfo Signal)> Signals() { lock (signals) return signals.ToList(); }
            int frames = 0;
            await using var engine = new FfmpegChannelEngine(locator, NullLogger.Instance) { OutputRoot = outputRoot };
            engine.FrameReady += (_, _) => Interlocked.Increment(ref frames);
            await engine.StartPreviewAsync(source, Profile(), "NET");
            await Task.Delay(1500); // el receptor ya escucha

            double startedAt = clock.Elapsed.TotalSeconds;
            using var sender = new Sender(TestAssets.FfmpegDir!, $"srt://127.0.0.1:{port}?mode=caller&latency=120000", "mpegts", 60, audioNeverSent: true);
            await WaitForAsync(() => Signals().Any(s => s.Signal.State == SignalState.Locked), Lock, "La fuente no dio SEÑAL OK con la pista de audio muda.");
            var (at, signal) = Signals().First(s => s.Signal.State == SignalState.Locked);
            double locked = at - startedAt;
            _out.WriteLine($"SEÑAL OK a los {locked:0.00} s ({signal.FormatLabel}{(signal.HasAudio ? ", con audio" : ", sin audio")}).");
            Assert.Equal("640×360 · 25p", signal.FormatLabel);
            Assert.False(signal.HasAudio); // la pista muda no llega al relé: el canal no debe mapear audio
            // El análisis espera a la pista muda hasta su tope, 12 s de vídeo (ver NetworkStreamReceiver.SrtAnalysisArguments).
            Assert.True(locked <= 16, $"SEÑAL OK a los {locked:0.00} s: con el tope del análisis (12 s) debía llegar en ≤ 16 s.");

            await WaitForAsync(() => Volatile.Read(ref frames) >= 3, Lock, "El preview no pintó el vídeo del emisor con la pista de audio muda.");
            _out.WriteLine($"Primer cuadro de preview a los {clock.Elapsed.TotalSeconds - startedAt:0.00} s.");
            var file = await RecordAndVerifyAsync(engine, locator, Profile(), TimeSpan.FromSeconds(3));
            var (video, audio) = await ProbeStartTimesAsync(locator.FfprobePath, file);
            _out.WriteLine($"Grabado: vídeo desde {video:0.000} s, {(audio is null ? "sin pista de audio" : $"audio desde {audio:0.000} s")}.");
            Assert.False(double.IsNaN(video), "La grabación no tiene vídeo.");

            // Sin parpadeo: desde SEÑAL OK la fuente no ha vuelto a SIN SEÑAL (el receptor ya no muere al escribir su cabecera).
            var afterLock = Signals().Where(s => s.At > at).ToList();
            Assert.True(afterLock.All(s => s.Signal.State == SignalState.Locked),
                "La señal parpadeó tras SEÑAL OK: " + string.Join(", ", afterLock.Select(s => $"{s.Signal.State} a los {s.At - startedAt:0.00} s")));
            Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        }
        finally { Cleanup(outputRoot); }
    }

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
