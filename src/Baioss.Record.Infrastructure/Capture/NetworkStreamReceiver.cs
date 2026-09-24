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
    }

    public int ConsumerPort => _relay.ConsumerPort;

    /// <summary>Puerto al que escribe el FFmpeg receptor (diagnóstico).</summary>
    public int SourcePort => _relay.SourcePort;

    public event EventHandler<FfmpegInputDescription>? PeerConnected;
    public event EventHandler? PeerLost;
    public event EventHandler? PassphraseRejected;

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
        _log.LogInformation("Receptor de red {Url}: {Args}", _input.Url, string.Join(' ', args));
        await _supervisor.StartAsync(args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Argumentos de ENTRADA para una definición válida. Las opciones van como argumentos APARTE (no en la URL): así
    /// una contraseña con «&amp;» o «=» no rompe nada y no hace falta escapar. Nombres medidos con el FFmpeg empaquetado:
    /// <c>-srt_streamid</c> (el <c>-streamid</c> a secas es una opción del CLI y choca), <c>-timeout</c> en SRT y
    /// <c>-rw_timeout</c> en RTMP solo acotan la E/S una vez conectados (la espera del emisor es ilimitada).
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
    /// paquete volcado al instante (<c>-flush_packets 1</c>: latencia mínima en el preview) y SIN retener paquetes para
    /// entrelazarlos por DTS (<c>-max_interleave_delta</c> de 50 ms en vez de los 10 s por defecto): con un emisor que
    /// trae el audio con otro reloj (visto en un servidor RTMP real: 3,4 h «por delante»), el muxer retenía el audio 10 s
    /// detrás del vídeo, y el relé, que alinea por orden de llegada, lo veía 10 s tarde. Con relojes coherentes no cambia nada.
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
            "-max_interleave_delta", "50000",
            "-f", "mpegts", "-flush_packets", "1",
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
