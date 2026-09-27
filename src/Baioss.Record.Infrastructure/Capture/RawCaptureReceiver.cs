using System.Globalization;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Receptor PERMANENTE de una captura de dispositivo (DeckLink): tiene la tarjeta abierta mientras la fuente esté
/// asignada y sirve su señal EN CRUDO al proceso del canal por un <see cref="RawStreamRelay"/>. Publica lo que FFmpeg
/// cuenta de la tarjeta (dispositivo abierto y descrito, modo detectado, tarjeta en uso, señal perdida/recuperada,
/// canales de audio rechazados). La interfaz existe para que la fuente se pruebe sin FFmpeg ni tarjeta.
/// </summary>
public interface IRawCaptureReceiver : IAsyncDisposable
{
    /// <summary>Puerto loopback del que el proceso del canal lee el NUT (<c>-f nut -i tcp://127.0.0.1:puerto</c>).</summary>
    int ConsumerPort { get; }

    /// <summary>FFmpeg abrió el dispositivo y describió su flujo (vídeo y si hay audio).</summary>
    event EventHandler<FfmpegInputDescription>? Opened;

    /// <summary>El proceso de captura terminó (la tarjeta se perdió, cambió, o no abrió) y se relanza por su cuenta.</summary>
    event EventHandler? Lost;

    /// <summary>DeckLink en autodetección: el modo que la tarjeta detectó en la señal, o null si no detectó nada.</summary>
    event EventHandler<DetectedVideoMode?>? ModeDetected;

    /// <summary>La tarjeta está en uso por otro proceso («Cannot enable video input»).</summary>
    event EventHandler? DeviceBusy;

    /// <summary>La señal en la entrada se perdió (false) o volvió (true), con la tarjeta abierta.</summary>
    event EventHandler<bool>? SignalPresence;

    /// <summary>La tarjeta rechazó los canales de audio pedidos («Cannot enable audio input»), una vez por intento.</summary>
    event EventHandler? AudioChannelsRejected;

    /// <summary>Reserva para el PRÓXIMO consumidor su alta en el relé y devuelve cómo saber si ya va al día.</summary>
    RelayReservation? ReserveConsumer() => null;

    /// <summary>Bytes que el relé ha repartido desde el arranque (diagnóstico).</summary>
    long ForwardedBytes => 0;

    /// <summary>Bytes que el relé ha recibido del proceso de captura desde el arranque (diagnóstico).</summary>
    long SourceBytes => 0;

    /// <summary>En qué está el bucle del relé que drena la captura (diagnóstico).</summary>
    string PumpStage => "";

    Task StartAsync(CancellationToken ct = default);

    /// <summary>Cierra el proceso de captura y lo vuelve a lanzar con los argumentos ACTUALES del dispositivo (p. ej. con
    /// menos canales de audio). Los consumidores del relé ven EOF y el motor los reconstruye.</summary>
    Task RestartAsync(CancellationToken ct = default);
}

/// <summary>
/// Implementación real: un FFmpeg supervisado que abre el dispositivo con sus argumentos de entrada y empuja el flujo
/// EN CRUDO en un contenedor NUT (vídeo tal cual lo entrega la tarjeta + PCM, con marcas de tiempo) al relé loopback.
/// Es la pieza que hace que Grabar/Detener no toquen la tarjeta: el proceso del canal (que sí se reemplaza en cada
/// Grabar/Detener) lee del relé, no del dispositivo, y el motor solapa el proceso nuevo con el viejo como con una entrada
/// de red: el preview no se congela.
/// <list type="bullet">
///   <item>Si el proceso termina (la tarjeta desapareció, cambió de modo, o no abrió: en uso, sin autodetección) el
///   supervisor lo relanza con su backoff; el relé cierra a los consumidores y la fuente pasa a SIN SEÑAL hasta que
///   vuelva a abrir.</item>
///   <item>El vigilante del supervisor no toma por colgado un proceso que aún no entregó nada (abrir una tarjeta y
///   autodetectar tarda hasta 3 s); una vez fluye, una tarjeta que deja de entregar frames se detecta por estancamiento
///   (30 s) y se reabre.</item>
///   <item>El estado sale del stderr de FFmpeg: el volcado «Input #0, decklink, from '…'» + pistas = dispositivo abierto
///   y formato real (<see cref="FfmpegInputDump"/>); las líneas del demuxer decklink (<see cref="DecklinkModeParser"/>)
///   = modo detectado, tarjeta en uso, señal perdida/recuperada, audio rechazado.</item>
/// </list>
/// </summary>
public sealed class RawCaptureReceiver : IRawCaptureReceiver
{
    private readonly string _name;
    private readonly Func<IReadOnlyList<string>> _deviceArguments;
    private readonly bool _copyStreams;
    private readonly string _ffmpegPath;
    private readonly ILogger _log;
    private readonly RawStreamRelay _relay;
    private readonly FfmpegInputDump _dump = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private FfmpegProcessSupervisor? _supervisor;
    private volatile bool _opened;
    private bool _audioRejectedThisRun;
    private string? _lastErrorLine;
    private DateTimeOffset _lastFailureLogUtc;

    /// <param name="name">Nombre para el registro (el dispositivo).</param>
    /// <param name="deviceArguments">Argumentos de ENTRADA del dispositivo (<c>-f decklink … -i "…"</c>), evaluados en cada
    /// lanzamiento (un relanzamiento tras bajar los canales de audio los lee de nuevo).</param>
    /// <param name="copyStreams">true para copiar los paquetes tal cual (DeckLink ya entrega vídeo en crudo y PCM);
    /// false para volcarlos a rawvideo/pcm_s16le (una fuente sintética en los tests).</param>
    public RawCaptureReceiver(string name, Func<IReadOnlyList<string>> deviceArguments, bool copyStreams, string ffmpegPath, ILogger log)
    {
        _name = name;
        _deviceArguments = deviceArguments;
        _copyStreams = copyStreams;
        _ffmpegPath = ffmpegPath;
        _log = log;
        _relay = new RawStreamRelay(name, log);
    }

    public int ConsumerPort => _relay.ConsumerPort;

    /// <summary>Puerto al que escribe el FFmpeg de captura (diagnóstico).</summary>
    public int SourcePort => _relay.SourcePort;

    public event EventHandler<FfmpegInputDescription>? Opened;
    public event EventHandler? Lost;
    public event EventHandler<DetectedVideoMode?>? ModeDetected;
    public event EventHandler? DeviceBusy;
    public event EventHandler<bool>? SignalPresence;
    public event EventHandler? AudioChannelsRejected;

    public RelayReservation? ReserveConsumer() => _relay.ReserveConsumer();

    public long ForwardedBytes => _relay.ForwardedBytes;

    public long SourceBytes => _relay.SourceBytes;

    public string PumpStage => _relay.PumpStage;

    /// <summary>Frames de vídeo repartidos por el relé (diagnóstico y tests).</summary>
    public long VideoFramesForwarded => _relay.VideoFramesForwarded;

    /// <summary>Consumidores dados de alta en el relé (diagnóstico y tests).</summary>
    public int ConsumerCount => _relay.ConsumerCount;

    /// <summary>Veces que el proceso de captura terminó y se relanzó desde el arranque (diagnóstico y tests: con la tarjeta
    /// sana debe quedarse en 0 por muchos Grabar/Detener que haya).</summary>
    public int Restarts { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_supervisor is not null) return;
            _relay.Start();
            await LaunchAsync(ct).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_supervisor is null) return; // nunca arrancó o ya se cerró
            await StopSupervisorAsync().ConfigureAwait(false);
            await LaunchAsync(ct).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        // FinalizeOnStop=false: no hay archivo que finalizar; al cerrar la fuente se mata al instante. StallTimeout 30 s
        // como el pipeline del canal. RestartOnCleanExit: un dispositivo no termina su flujo por las buenas.
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
        var args = ArgumentsFor(_deviceArguments(), _copyStreams, _relay.SourcePort);
        _log.LogInformation("Receptor de captura {Name}: {Args}", _name, string.Join(' ', args));
        _dump.Reset();
        _opened = false;
        _audioRejectedThisRun = false;
        await _supervisor.StartAsync(args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// La línea de comandos completa del receptor: la entrada del dispositivo → el primer vídeo y, si lo hay, el primer
    /// audio (<c>-map 0:a:0?</c>) → sin recodificar (<c>-c copy</c>: DeckLink ya entrega vídeo en crudo y PCM; con una
    /// fuente sintética, a rawvideo/pcm_s16le) → NUT por TCP al relé, con cada frame volcado al instante
    /// (<c>-flush_packets 1</c>). Pura y testeable.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsFor(IReadOnlyList<string> deviceArguments, bool copyStreams, int sourcePort)
    {
        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        args.AddRange(deviceArguments);
        args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });
        if (copyStreams) args.AddRange(new[] { "-c", "copy" });
        else args.AddRange(new[] { "-c:v", "rawvideo", "-c:a", "pcm_s16le" });
        args.AddRange(new[]
        {
            "-f", "nut", "-flush_packets", "1",
            $"tcp://127.0.0.1:{sourcePort.ToString(CultureInfo.InvariantCulture)}?tcp_nodelay=1",
        });
        return args;
    }

    private void OnLog(object? sender, string line)
    {
        if (DecklinkModeParser.TryParseFoundMode(line) is { } mode) { ModeDetected?.Invoke(this, mode); return; }
        if (DecklinkModeParser.IsAutodetectFailure(line))
        {
            _lastErrorLine = line.Trim();
            ModeDetected?.Invoke(this, null);
            return;
        }
        if (DecklinkModeParser.IsDeviceBusy(line))
        {
            _lastErrorLine = line.Trim();
            DeviceBusy?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (DecklinkModeParser.IsAudioInputRejected(line))
        {
            _lastErrorLine = line.Trim();
            if (_audioRejectedThisRun) return;
            _audioRejectedThisRun = true;
            AudioChannelsRejected?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (DecklinkModeParser.IsNoInputSignal(line)) { SignalPresence?.Invoke(this, false); return; }
        if (DecklinkModeParser.IsInputReturned(line)) { SignalPresence?.Invoke(this, true); return; }
        if (!_opened && line.Contains("rror", StringComparison.Ordinal)) _lastErrorLine = line.Trim();

        var description = _dump.Feed(line);
        if (description is null) return;
        _opened = true;
        _lastErrorLine = null;
        _log.LogInformation("Captura {Name}: dispositivo abierto ({Format}{Audio}).", _name,
            description.Video?.Label ?? "formato sin describir", description.HasAudio ? ", con audio" : ", sin audio");
        Opened?.Invoke(this, description);
    }

    private void OnRestarted(object? sender, int restartCount)
    {
        Restarts++;
        _dump.Reset();
        bool wasOpen = _opened;
        _opened = false;
        _audioRejectedThisRun = false;
        if (wasOpen)
        {
            _log.LogWarning("Captura {Name}: el proceso de captura terminó; se reabre el dispositivo.", _name);
        }
        else if (_lastErrorLine is not null)
        {
            // Nunca llegó a abrir: tarjeta en uso, sin señal en autodetección, driver ausente… El supervisor reintenta
            // solo; aquí queda POR QUÉ, con freno para no inundar el registro con reintentos cada pocos segundos.
            var now = DateTimeOffset.UtcNow;
            if (now - _lastFailureLogUtc > TimeSpan.FromSeconds(30))
            {
                _lastFailureLogUtc = now;
                _log.LogWarning("Captura {Name}: no se pudo abrir el dispositivo ({Reason}); se reintenta.", _name, _lastErrorLine);
            }
            _lastErrorLine = null;
        }
        Lost?.Invoke(this, EventArgs.Empty);
    }

    private async Task StopSupervisorAsync()
    {
        var supervisor = _supervisor;
        if (supervisor is null) return;
        _supervisor = null;
        supervisor.LogLine -= OnLog;
        supervisor.Restarted -= OnRestarted;
        await supervisor.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>SOLO PARA TESTS: mata el proceso de captura para simular la pérdida de la tarjeta.</summary>
    internal bool KillProcessForTest() => _supervisor?.KillCurrentProcessForTest() ?? false;

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopSupervisorAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
        await _relay.DisposeAsync().ConfigureAwait(false);
    }
}
