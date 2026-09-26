using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Fuente de captura de un flujo de RED (SRT o RTMP) que se GRABA como cualquier otra entrada. Igual que NDI, la fuente
/// no la abre el proceso del canal directamente: un <see cref="INetworkStreamReceiver"/> PERMANENTE mantiene la
/// conexión con el emisor y sirve el flujo (MPEG-TS, sin recodificar) por un relé loopback, y el pipeline de siempre
/// (preview + medidores + grabación con el preset) lo lee de ahí como una entrada más. Así:
/// <list type="bullet">
///   <item>Grabar y Detener —que reemplazan el proceso del canal— NO tocan la conexión con el emisor: OBS/vMix/el
///   codificador no ven ningún corte y la grabación arranca con el pre-roll del relé, sin perder el principio.</item>
///   <item>La señal empieza en SIN SEÑAL («esperando al emisor» / «conectando…») y pasa a SEÑAL OK cuando el receptor ve
///   al emisor, con el formato real y si trae audio; vuelve a SIN SEÑAL cuando el flujo termina. El botón Grabar solo
///   se habilita con flujo de verdad, y el pipeline no se construye hasta que lo hay (el motor espera y lo levanta solo).</item>
///   <item>La fuente se AUTO-REPORTA (<see cref="SelfReportsRecovery"/>): el motor no sondea el dispositivo en carta de
///   ajuste —sondearlo abriría un ffmpeg contra el relé, no contra el emisor—; sale del slate cuando esta señal vuelve.</item>
/// </list>
/// </summary>
public sealed class NetworkStreamCaptureSource : ICaptureSource
{
    private readonly Func<NetworkInput, INetworkStreamReceiver> _receiverFactory;
    private readonly NetworkInput? _input;
    private readonly NetworkInputError _error;
    private readonly object _sync = new();
    private INetworkStreamReceiver? _receiver;
    private bool _rejected;

    public NetworkStreamCaptureSource(InputSource definition, Func<NetworkInput, INetworkStreamReceiver> receiverFactory)
    {
        Definition = definition;
        _receiverFactory = receiverFactory;
        _input = NetworkInput.FromInputSource(definition);
        _error = _input?.Validate() ?? NetworkInputError.InvalidUrl;
    }

    public InputSource Definition { get; }
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

    /// <summary>La definición de red de esta entrada (null si la entrada guardada no se entiende).</summary>
    public NetworkInput? Input => _input;

    /// <summary>¿Hay receptor en marcha? (diagnóstico y tests)</summary>
    public bool IsOpen { get { lock (_sync) return _receiver is not null; } }

    /// <summary>El flujo trae el audio que trae (normalmente estéreo); no hay pares que elegir.</summary>
    public int AudioChannelCount => 2;

    /// <summary>El receptor permanente publica la pérdida y la vuelta del emisor: el motor no debe sondear.</summary>
    public bool SelfReportsRecovery => true;

    /// <summary>El proceso del canal lee del relé y puede quedarse esperando flujo sin producir nada (tras un fin de flujo,
    /// hasta que el emisor vuelva): no es un proceso colgado.</summary>
    public bool WaitsForPeer => true;

    /// <summary>Cuando el emisor cierra, el relé cierra al proceso del canal, que sale con código 0: en preview hay que
    /// relanzarlo para que vuelva a esperar el flujo siguiente.</summary>
    public bool RestartsAfterEndOfStream => true;

    /// <summary>Arranca el receptor permanente (idempotente: el bucle de espera del motor reabre la fuente cada pocos
    /// segundos y NO debe reiniciar un receptor que ya escucha, se cerraría el puerto al emisor).</summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (_input is null || _error != NetworkInputError.None)
            throw new InvalidOperationException(Localizer.F("Net_Err_CannotOpen", Definition.Name, Localizer.T(ErrorKey(_error))));

        INetworkStreamReceiver receiver;
        SignalInfo signal;
        lock (_sync)
        {
            if (_receiver is not null) return;
            receiver = _receiver = _receiverFactory(_input);
            receiver.PeerConnected += OnPeerConnected;
            receiver.PeerLost += OnPeerLost;
            receiver.PassphraseRejected += OnPassphraseRejected;
            _rejected = false;
            // Sin señal hasta que el receptor vea al emisor: con «SEÑAL OK» optimista el operador podría pulsar Grabar sin
            // que haya emisor y creer que graba algo.
            signal = CurrentSignal = Waiting();
        }
        SignalChanged?.Invoke(this, signal);
        try { await receiver.StartAsync(ct).ConfigureAwait(false); }
        catch
        {
            await StopReceiverAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task CloseAsync(CancellationToken ct = default) => StopReceiverAsync();

    public async ValueTask DisposeAsync() => await StopReceiverAsync().ConfigureAwait(false);

    private async Task StopReceiverAsync()
    {
        INetworkStreamReceiver? receiver;
        lock (_sync)
        {
            receiver = _receiver;
            _receiver = null;
            if (receiver is not null)
            {
                receiver.PeerConnected -= OnPeerConnected;
                receiver.PeerLost -= OnPeerLost;
                receiver.PassphraseRejected -= OnPassphraseRejected;
            }
            CurrentSignal = SignalInfo.None; // sin avisar: al cerrar (reasignación/apagado) ya nadie escucha esta fuente
        }
        if (receiver is not null) await receiver.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Entrada del proceso del canal: el MPEG-TS que sirve el relé del receptor. Solo se puede construir con el emisor
    /// conectado (como NDI sin receptor): antes, el motor entra en espera y levanta el pipeline cuando llega la señal.
    /// </summary>
    public IReadOnlyList<string> BuildInputArguments()
    {
        if (_input is null || _error != NetworkInputError.None)
            throw new InvalidOperationException(Localizer.F("Net_Err_CannotOpen", Definition.Name, Localizer.T(ErrorKey(_error))));
        INetworkStreamReceiver? receiver;
        SignalInfo signal;
        lock (_sync) { receiver = _receiver; signal = CurrentSignal; }
        if (receiver is null || signal.State != SignalState.Locked)
            throw new InvalidOperationException(Localizer.F("Net_Err_CannotOpen", Definition.Name, Localizer.T("Net_Err_NoPeer")));
        // Reserva la instantánea del pre-roll para el proceso que se va a construir: así el número de frames que su
        // preview debe saltarse es exacto (el relé no la recorta ni la amplía entre este momento y la conexión).
        _reservation = receiver.ReserveConsumer();
        return ConsumerArgumentsFor(receiver.ConsumerPort);
    }

    /// <summary>El relé reparte el flujo a varios consumidores: el motor puede arrancar el proceso nuevo antes de retirar el viejo.</summary>
    public bool SupportsOverlappingProcesses => true;

    /// <summary>¿El proceso del último <see cref="BuildInputArguments"/> ya lee el flujo en directo (agotó el pre-roll y el atraso
    /// acumulado mientras lo digería)? Hasta entonces su preview va adelantado y el motor no le cede el mando.</summary>
    public bool NewestConsumerIsLive => _reservation?.IsLive ?? true;

    /// <summary>Colchón de preview configurado en la fuente (Entradas → Fuentes de red), 0 si no tiene.</summary>
    public int PreviewBufferMs => _input?.PreviewBufferMs ?? 0;

    /// <summary>Bytes repartidos por el relé al proceso del canal desde que se abrió la fuente (diagnóstico y tests: distingue
    /// «el emisor dejó de entregar» de «el motor dejó de pintar»).</summary>
    public long RelayForwardedBytes { get { lock (_sync) return _receiver?.ForwardedBytes ?? 0; } }

    /// <summary>Bytes que el relé ha recibido del receptor (diagnóstico: con <see cref="RelayForwardedBytes"/> dice si el relé retiene).</summary>
    public long RelaySourceBytes { get { lock (_sync) return _receiver?.SourceBytes ?? 0; } }

    /// <summary>En qué está el bucle del relé que drena al receptor (diagnóstico).</summary>
    public string RelayPumpStage { get { lock (_sync) return _receiver?.PumpStage ?? ""; } }

    /// <summary>Frames de vídeo del pre-roll reservado en el último <see cref="BuildInputArguments"/>: la grabación los
    /// necesita (empieza antes del botón), el preview no debe mostrarlos.</summary>
    public int PreviewFramesToSkip => _reservation?.VideoFrames ?? 0;
    private RelayReservation? _reservation;

    /// <summary>Puro y testeable: la entrada TS por TCP loopback. Formato explícito (sin adivinar). Con MPEG-TS, FFmpeg
    /// AGOTA SIEMPRE el tiempo de análisis (el formato no tiene cabecera: no sabe cuándo ha visto todas las pistas), así
    /// que son 2 s de FLUJO, que el pre-roll del relé (≥ 2,5 s desde un fotograma clave) cubre al instante: el proceso
    /// nuevo pinta y graba enseguida. Con 5 s consumía el pre-roll y esperaba 2,5 s en vivo: 3 s de preview congelado en
    /// cada Grabar/Detener (medido). El caso «audio con otro reloj» ya no exige más: el relé retiene también el vídeo
    /// mientras mide, así que ningún proceso ve un flujo solo-vídeo.</summary>
    public static IReadOnlyList<string> ConsumerArgumentsFor(int consumerPort) => new[]
    {
        "-f", "mpegts", "-analyzeduration", "2000000", "-probesize", "5000000",
        "-i", $"tcp://127.0.0.1:{consumerPort}",
    };

    /// <summary>Se ignora: el estado lo dicta el receptor permanente. Lo que el proceso del canal cuente de SU entrada
    /// (el relé en <c>tcp://127.0.0.1</c>) no habla del emisor.</summary>
    public void ReportDeviceOpen(bool opened) { }

    /// <summary>Se ignora: el formato real lo publica el receptor al describir el flujo del emisor.</summary>
    public void ReportDetectedMode(DetectedVideoMode? mode) { }

    private void OnPeerConnected(object? sender, FfmpegInputDescription description)
    {
        SignalInfo signal;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _receiver)) return; // un receptor ya cerrado
            _rejected = false;
            signal = CurrentSignal = new SignalInfo(SignalState.Locked, description.Video?.Resolution, description.Video?.FrameRate,
                AudioLayout.Stereo, HasAudio: description.HasAudio, Timecode: null, Bitrate: null, FormatLabel: description.Video?.Label);
        }
        SignalChanged?.Invoke(this, signal);
    }

    private void OnPeerLost(object? sender, EventArgs e)
    {
        SignalInfo signal;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _receiver)) return;
            var waiting = Waiting();
            if (CurrentSignal.State == SignalState.NoSignal && CurrentSignal.FormatLabel == waiting.FormatLabel) return; // sin ruido
            signal = CurrentSignal = waiting;
        }
        SignalChanged?.Invoke(this, signal);
    }

    private void OnPassphraseRejected(object? sender, EventArgs e)
    {
        SignalInfo signal;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _receiver)) return;
            if (CurrentSignal.State == SignalState.Locked) return; // ya hay un emisor bueno conectado: el rechazo de otro no cuenta
            _rejected = true;
            var waiting = Waiting();
            if (CurrentSignal.FormatLabel == waiting.FormatLabel) return;
            signal = CurrentSignal = waiting;
        }
        SignalChanged?.Invoke(this, signal);
    }

    /// <summary>Sin señal, con el motivo en palabras: esperando (escucha) / conectando (llamada), o «contraseña
    /// rechazada» desde que SRT rechazó a un emisor hasta que conecta uno con la contraseña buena.</summary>
    private SignalInfo Waiting() => new(SignalState.NoSignal, null, null, AudioLayout.Stereo, HasAudio: true,
        Timecode: null, Bitrate: null, FormatLabel: WaitingLabel());

    private string WaitingLabel() => Localizer.T(_rejected ? "Net_State_BadPassphrase"
        : _input?.Role == NetworkRole.Listen ? "Net_State_Waiting" : "Net_State_Connecting");

    internal static string ErrorKey(NetworkInputError error) => error switch
    {
        NetworkInputError.MissingHost => "Net_Err_MissingHost",
        NetworkInputError.InvalidHost => "Net_Err_InvalidHost",
        NetworkInputError.InvalidPort => "Net_Err_InvalidPort",
        NetworkInputError.InvalidPath => "Net_Err_InvalidPath",
        NetworkInputError.PassphraseLength => "Net_Err_Passphrase",
        NetworkInputError.LatencyRange => "Net_Err_Latency",
        NetworkInputError.StreamIdTooLong => "Net_Err_StreamId",
        NetworkInputError.AudioDelayRange => "Net_Err_AudioDelay",
        NetworkInputError.UnsupportedScheme => "Net_Err_Scheme",
        _ => "Net_Err_InvalidUrl",
    };
}

/// <summary>Fábrica de las entradas de red: SRT (escucha o llamada) y RTMP (servidor o cliente). El receptor permanente
/// es un FFmpeg, así que necesita el localizador (solo se registra cuando hay FFmpeg).</summary>
public sealed class NetworkStreamCaptureSourceFactory(IFfmpegLocator ffmpeg, ILoggerFactory loggers) : ICaptureSourceFactory
{
    public bool CanHandle(InputType type) => NetworkInput.IsNetworkType(type);

    public ICaptureSource Create(InputSource definition) => new NetworkStreamCaptureSource(definition,
        input => new NetworkStreamReceiver(input, ffmpeg.FfmpegPath, loggers.CreateLogger<NetworkStreamReceiver>()));
}
