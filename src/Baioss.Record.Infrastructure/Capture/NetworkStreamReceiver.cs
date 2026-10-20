using System.Globalization;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Receptor PERMANENTE de una entrada de red: mantiene la conexión con el emisor (SRT en escucha o llamada, RTMP como
/// servidor o cliente) mientras la fuente esté abierta, y sirve el flujo al proceso del canal por un
/// <see cref="NetworkStreamRelay"/>. Publica lo que el emisor entrega (conectado y descrito, perdido, rechazado).
/// La interfaz existe para que la fuente se pruebe sin FFmpeg ni red.
/// </summary>
public interface INetworkStreamReceiver : IAsyncDisposable
{
    /// <summary>Puerto loopback del que el proceso del canal lee el MPEG-TS (<c>-i tcp://127.0.0.1:puerto</c>).</summary>
    int ConsumerPort { get; }

    /// <summary>El emisor conectó y FFmpeg describió el flujo (vídeo y si hay audio).</summary>
    event EventHandler<FfmpegInputDescription>? PeerConnected;

    /// <summary>El flujo terminó o la conexión falló; el receptor vuelve a esperar/llamar por su cuenta.</summary>
    event EventHandler? PeerLost;

    /// <summary>SRT rechazó la conexión por contraseña incorrecta (la del emisor no coincide con la de la entrada).</summary>
    event EventHandler? PassphraseRejected;

    /// <summary>Reserva para el PRÓXIMO consumidor la instantánea del pre-roll de este instante: cuántos frames de vídeo
    /// contiene (los que su preview debe saltarse) y si ese consumidor ya va al día. Ver <see cref="NetworkStreamRelay.ReserveConsumer"/>.</summary>
    RelayReservation? ReserveConsumer() => null;

    /// <summary>Bytes que el relé ha repartido a los consumidores desde el arranque (diagnóstico: ¿sigue llegando flujo del emisor?).</summary>
    long ForwardedBytes => 0;

    /// <summary>Bytes que el relé ha recibido del receptor desde el arranque (diagnóstico).</summary>
    long SourceBytes => 0;

    /// <summary>En qué está el bucle del relé que drena al receptor (diagnóstico). Ver <see cref="NetworkStreamRelay.PumpStage"/>.</summary>
    string PumpStage => "";

    /// <summary>Frames de vídeo que el relé ha repartido desde el arranque: el número del directo (diagnóstico y tests).</summary>
    long VideoFramesForwarded => 0;

    /// <summary>El relé descartó datos de un proceso del canal que no daba abasto (ver <see cref="NetworkStreamRelay.ConsumerOverflow"/>).</summary>
    event EventHandler<long>? DataDropped { add { } remove { } }

    Task StartAsync(CancellationToken ct = default);
}

/// <summary>
/// Implementación real: un FFmpeg supervisado que abre la URL con las opciones del protocolo y COPIA el flujo sin
/// recodificar (<c>-c copy -f mpegts</c>) al relé. Es la pieza que hace que Grabar/Detener no toquen la conexión con
/// el emisor: el proceso del canal (que sí se reemplaza en cada Grabar/Detener) lee del relé, no del emisor.
/// <list type="bullet">
///   <item>Un fin de flujo (el emisor cerró: FFmpeg sale con 0) o un fallo (emisor caído, contraseña incorrecta, puerto
///   ocupado…) relanza el receptor con el backoff del supervisor: en escucha vuelve a esperar, en llamada reintenta.</item>
///   <item>El vigilante del supervisor no toma por colgado a un receptor que espera al emisor sin producir nada
///   (<see cref="FfmpegProcessSupervisor.IgnoreStallUntilFirstProgress"/>); una vez fluye, un emisor mudo se detecta por
///   los timeouts de E/S de <see cref="IoTimeoutSeconds"/> s (y, como red, por el vigilante a los 30 s).</item>
///   <item>El estado sale del stderr de FFmpeg: el volcado «Input #0 … from 'url'» + pistas = emisor conectado y
///   formato real (<see cref="FfmpegInputDump"/>); «Incorrect passphrase» = rechazo SRT; el relanzamiento = perdido.</item>
/// </list>
/// </summary>
public sealed class NetworkStreamReceiver : INetworkStreamReceiver
{
    public const int IoTimeoutSeconds = 10;

    private readonly NetworkInput _input;
    private readonly string _ffmpegPath;
    private readonly ILogger _log;
    private readonly NetworkStreamRelay _relay;
    private readonly FfmpegInputDump _dump = new();
    private FfmpegProcessSupervisor? _supervisor;
    private volatile bool _connected;
    private string? _lastErrorLine;
    private DateTimeOffset _lastFailureLogUtc;
    private DateTimeOffset _lastRejectLogUtc;

    public NetworkStreamReceiver(NetworkInput input, string ffmpegPath, ILogger log)
    {
        _input = input.Normalized();
        _ffmpegPath = ffmpegPath;
        _log = log;
        _relay = new NetworkStreamRelay(_input.Url, log) { AudioDelayMs = _input.AudioDelayMs };
        _relay.ConsumerOverflow += (_, dropped) => DataDropped?.Invoke(this, dropped);
    }

    public event EventHandler<long>? DataDropped;
    private int _disposed;

    public int ConsumerPort => _relay.ConsumerPort;

    /// <summary>Puerto al que escribe el FFmpeg receptor (diagnóstico).</summary>
    public int SourcePort => _relay.SourcePort;

    public event EventHandler<FfmpegInputDescription>? PeerConnected;
    public event EventHandler? PeerLost;
    public event EventHandler? PassphraseRejected;

    public RelayReservation? ReserveConsumer() => _relay.ReserveConsumer();

    public long ForwardedBytes => _relay.ForwardedBytes;

    public long SourceBytes => _relay.SourceBytes;

    public string PumpStage => _relay.PumpStage;

    public long VideoFramesForwarded => _relay.VideoFramesForwarded;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_supervisor is not null) return;
        _relay.Start();
        // FinalizeOnStop=false: no hay archivo que finalizar; al cerrar la fuente se mata al instante (un receptor en
        // escucha ignora la «q» mientras espera). StallTimeout 30 s como el pipeline del canal.
        _supervisor = new FfmpegProcessSupervisor(_ffmpegPath, _log)
        {
            StallTimeout = TimeSpan.FromSeconds(30),
            FinalizeOnStop = false,
            RestartInternally = true,
            RestartOnCleanExit = true,
            IgnoreStallUntilFirstProgress = true,
        };
        _supervisor.LogLine += OnLog;
        _supervisor.Restarted += OnRestarted;
        var args = ArgumentsFor(_input, _relay.SourcePort);
        _log.LogInformation("Receptor de red {Url}: {Args}", _input.Url, string.Join(' ', Redacted(args)));
        // La vida del receptor es la de la fuente (termina en DisposeAsync), no la de quien la abrió.
        await _supervisor.StartAsync(args, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Los argumentos para el REGISTRO, con los secretos tapados: la contraseña SRT (y el streamid, que en algunos servidores
    /// lleva un token) acababan en claro en el archivo de log y en cualquier copia que se enviara a soporte.
    /// </summary>
    public static IEnumerable<string> Redacted(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            yield return args[i];
            if (args[i] is "-passphrase" or "-srt_streamid" && i + 1 < args.Count) { yield return "***"; i++; }
        }
    }

    /// <summary>
    /// Análisis de la entrada SRT (opciones de entrada, antes de <c>-i</c>): el receptor vuelca la descripción —y la fuente pasa
    /// a SEÑAL OK con el formato real— EN CUANTO FFmpeg conoce cada pista, no al agotar un tiempo fijo:
    /// <list type="bullet">
    ///   <item><c>-scan_all_pmts 0</c>: el CLI de FFmpeg pone 1 en toda entrada que no lo diga, y con 1 el MPEG-TS cuenta como
    ///   formato SIN CABECERA durante todo el análisis (podría aparecer otra pista), así que FFmpeg lo agotaba SIEMPRE: 5 s de
    ///   flujo, o los 5 MB de sondeo si llegaban antes. Con 0, en cuanto cada programa tiene su PMT el demuxer da la cabecera
    ///   por vista y el análisis acaba al tener todas las pistas descritas: el primer fotograma clave (tamaño, formato de píxel,
    ///   barrido), 20 frames con marca de tiempo (la cadencia) y una trama de audio decodificada. Medido con un emisor SRT local
    ///   (HEVC 1080p29.97 + AAC) entrando justo detrás de un fotograma clave, el peor momento: GOP 2 s, 5,7 → 2,5 s; GOP 4 s,
    ///   5,7 → 4,4 s; con el clave al llegar, 5,6 → 1,3 s; MPEG-2 1080i, 2,8 → 1,0 s, con su «top first»; un audio que empieza
    ///   3 s tarde, 6,7 → 3,7 s, ya descrito.</item>
    ///   <item><c>-analyzeduration</c> 12 s y <c>-probesize</c> 64 MB: el TOPE para lo que no se deja describir antes. Con los de
    ///   siempre (7 s si falta alguna pista, 5 MB) un GOP de 8–10 s —el de x264 y OBS por defecto: 250 frames— daba SEÑAL OK a
    ///   los 6,8 s SIN resolución («unspecified size»), y a 15–20 Mb/s los 5 MB se acaban en 2–3 s. Medido: GOP 8 s, 8,2 s; GOP
    ///   10 s, 10,2 s; los dos con el formato entero. Lo que nunca llega a describirse (una pista de audio en la PMT que no trae
    ///   paquetes) espera el tope entero.</item>
    ///   <item><c>-f mpegts</c>: <c>-scan_all_pmts</c> es una opción del demuxer MPEG-TS y el CLI ABORTA si una opción de entrada
    ///   no la usa nadie. Lo que viaja por SRT es MPEG-TS (y el relé y el proceso del canal solo entienden eso).</item>
    /// </list>
    /// RTMP no lo necesita: la cabecera FLV anuncia sus pistas y FFmpeg acaba en cuanto las describe (0,2–1,9 s con el servidor real).
    /// </summary>
    public static readonly IReadOnlyList<string> SrtAnalysisArguments = new[]
    {
        "-f", "mpegts", "-scan_all_pmts", "0", "-analyzeduration", "12000000", "-probesize", "64000000",
    };

    /// <summary>
    /// Argumentos de ENTRADA para una definición válida. Las opciones van como argumentos APARTE (no en la URL): así
    /// una contraseña con «&amp;» o «=» no rompe nada y no hace falta escapar. Nombres medidos con el FFmpeg empaquetado:
    /// <c>-srt_streamid</c> (el <c>-streamid</c> a secas es una opción del CLI y choca), <c>-timeout</c> en SRT y
    /// <c>-rw_timeout</c> en RTMP solo acotan la E/S una vez conectados (la espera del emisor es ilimitada). SRT lleva además
    /// su análisis (<see cref="SrtAnalysisArguments"/>).
    /// </summary>
    public static IReadOnlyList<string> InputArgumentsFor(NetworkInput input)
    {
        var n = input.Normalized();
        var args = new List<string>();
        string ioTimeoutMicros = (IoTimeoutSeconds * 1_000_000L).ToString(CultureInfo.InvariantCulture);
        if (n.Protocol == NetworkProtocol.Srt)
        {
            args.AddRange(new[] { "-mode", n.Role == NetworkRole.Listen ? "listener" : "caller" });
            args.AddRange(new[] { "-latency", (n.LatencyMs * 1000L).ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-timeout", ioTimeoutMicros });
            if (n.Passphrase is not null) args.AddRange(new[] { "-passphrase", n.Passphrase });
            if (n.Role == NetworkRole.Connect && n.StreamId is not null) args.AddRange(new[] { "-srt_streamid", n.StreamId });
            args.AddRange(SrtAnalysisArguments);
        }
        else if (n.Role == NetworkRole.Listen)
        {
            // Servidor RTMP mínimo de FFmpeg: acepta UN emisor que publique en esa aplicación/clave; -timeout -1 = espera
            // al emisor sin límite; -rw_timeout acota la E/S una vez conectado.
            args.AddRange(new[] { "-listen", "1", "-timeout", "-1", "-rw_timeout", ioTimeoutMicros });
        }
        else
        {
            args.AddRange(new[] { "-rtmp_live", "live", "-rw_timeout", ioTimeoutMicros });
        }
        args.Add("-i");
        args.Add(n.Url);
        return args;
    }

    /// <summary>
    /// La línea de comandos completa del receptor: entrada del protocolo → el primer vídeo y, si lo hay, el primer audio
    /// (<c>-map 0:a:0?</c>: un flujo solo-vídeo no aborta) → copia sin recodificar a MPEG-TS por TCP al relé, con cada
    /// paquete volcado al instante (<c>-flush_packets 1</c>: latencia mínima en el preview). Cada trama de audio sale EN
    /// CUANTO LLEGA, porque con un emisor que trae el audio con otro reloj (visto en servidores RTMP reales: 3,4 h y 3056 s
    /// «por delante») el relé lo realinea por ORDEN DE LLEGADA, y lo que el muxer retrasa se convierte en desfase de labios:
    /// <list type="bullet">
    ///   <item>sin retenerla para entrelazarla por DTS (<c>-max_interleave_delta</c> de 1 µs; por defecto son 10 s, que
    ///   dejaban el audio 10 s tarde, y con 50 ms aún llegaba ~2–3 tramas tarde);</item>
    ///   <item>sola en su PES: por defecto el muxer junta tramas AAC hasta ~2,9 KB (8 tramas, 170 ms a 135 kb/s) y suelta el
    ///   PES al llenarlo, con el PTS de la primera; el relé, que mide cada PES por ese PTS, veía el audio menos adelantado de
    ///   lo que iba y lo colocaba tarde (en el servidor RTMP real, el orden de llegada daba 3056,519 s y el TS del receptor
    ///   3056,247 s: 0,27 s; con el emisor de las pruebas, a 96 kb/s, el pitido sonaba 0,47 s después del destello).
    ///   <c>-pes_payload_size 0</c> deja sola cada trama de más de 170 bytes; las menores (un silencio son ~20 bytes) las sigue
    ///   juntando hasta media <c>-muxdelay</c> (350 ms por defecto), así que también <c>-muxdelay 0</c>: cada trama sale cuando
    ///   llega la siguiente. Queda una trama de espera en cada cola (~42 ms con AAC a 48 kHz, medido). El PCR, que con
    ///   <c>-muxdelay 0</c> va pegado al DTS, no lo usa nadie: el relé y los procesos del canal (FFmpeg) se guían por PTS/DTS.</item>
    /// </list>
    /// Con relojes coherentes mandan las marcas de tiempo y nada de esto cambia lo grabado.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsFor(NetworkInput input, int sourcePort)
    {
        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        args.AddRange(InputArgumentsFor(input));
        args.AddRange(new[]
        {
            "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy",
            // Marcas de tiempo TAL CUAL (sin re-basar ni «corregir»): con una entrada mpegts (SRT) y el audio con otro
            // reloj, FFmpeg aplicaba su corrección de discontinuidades por PAQUETE, alternando el desplazamiento entre
            // vídeo y audio con un aviso por paquete; el relé realinea el audio en un solo sitio, para SRT y RTMP igual.
            "-copyts",
            "-max_interleave_delta", "1",
            "-f", "mpegts", "-flush_packets", "1", "-pes_payload_size", "0", "-muxdelay", "0",
            $"tcp://127.0.0.1:{sourcePort.ToString(CultureInfo.InvariantCulture)}?tcp_nodelay=1",
        });
        return args;
    }

    private void OnLog(object? sender, string line)
    {
        // libsrt cuenta el rechazo del handshake («UU:newConnection: rsp(REJECT): 1010 - Incorrect passphrase»); luego
        // FFmpeg falla con -138 y el supervisor relanza. Se avisa con freno: un emisor mal configurado reintenta en bucle.
        if (line.Contains("Incorrect passphrase", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastRejectLogUtc > TimeSpan.FromSeconds(30))
            {
                _lastRejectLogUtc = now;
                _log.LogWarning("Entrada de red {Url}: el emisor usa otra contraseña SRT; conexión rechazada (se sigue esperando).", _input.Url);
            }
            PassphraseRejected?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!_connected && line.Contains("rror", StringComparison.Ordinal)) _lastErrorLine = line.Trim();

        var description = _dump.Feed(line);
        if (description is null || !string.Equals(description.Url, _input.Url, StringComparison.Ordinal)) return;
        _connected = true;
        _lastErrorLine = null;
        _log.LogInformation("Entrada de red {Url}: emisor conectado ({Format}{Audio}).", _input.Url,
            description.Video?.Label ?? "formato sin describir", description.HasAudio ? ", con audio" : ", sin audio");
        PeerConnected?.Invoke(this, description);
    }

    private void OnRestarted(object? sender, int restartCount)
    {
        _dump.Reset();
        bool wasConnected = _connected;
        _connected = false;
        if (wasConnected)
        {
            _log.LogInformation("Entrada de red {Url}: el emisor cerró el flujo; se vuelve a esperar.", _input.Url);
        }
        else if (_lastErrorLine is not null)
        {
            // Nunca llegó a abrir: emisor/servidor caído, puerto ocupado por otro programa, DNS… El supervisor
            // reintenta solo; aquí queda POR QUÉ, con freno para no inundar el log en llamadas que fallan cada pocos segundos.
            var now = DateTimeOffset.UtcNow;
            if (now - _lastFailureLogUtc > TimeSpan.FromSeconds(30))
            {
                _lastFailureLogUtc = now;
                _log.LogWarning("Entrada de red {Url}: no se pudo abrir ({Reason}); se reintenta.", _input.Url, _lastErrorLine);
            }
            _lastErrorLine = null;
        }
        PeerLost?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotente
        if (_supervisor is not null)
        {
            _supervisor.LogLine -= OnLog;
            _supervisor.Restarted -= OnRestarted;
            await _supervisor.DisposeAsync().ConfigureAwait(false);
            _supervisor = null;
        }
        await _relay.DisposeAsync().ConfigureAwait(false);
    }
}
