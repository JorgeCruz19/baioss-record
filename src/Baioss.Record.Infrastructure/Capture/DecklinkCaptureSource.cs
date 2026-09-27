using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Fuente de captura DeckLink (SDI). Dos modos:
/// <list type="bullet">
///   <item><b>Dispositivo persistente con relé (por defecto):</b> un <see cref="IRawCaptureReceiver"/> PERMANENTE tiene
///   la tarjeta abierta mientras la fuente esté asignada y sirve su señal EN CRUDO (NUT por loopback) al proceso del
///   canal, que la lee como una entrada más. Grabar y Detener —que reemplazan el proceso del canal— ya NO reabren la
///   tarjeta (cada apertura costaba 0,3–1 s sin frames, más con autodetección, y con el conector bidireccional de la
///   Duo 2 la resincronización del receptor SDI), y el motor solapa el proceso nuevo con el viejo como con una entrada
///   de red: el preview no se congela. La señal la dicta el receptor: SIN SEÑAL mientras abre la tarjeta (o si está en
///   uso, o no detecta nada, o la entrada se quedó sin señal), SEÑAL OK con el formato real cuando captura.</item>
///   <item><b>Captura directa (sin relé):</b> el proceso del canal abre la tarjeta él mismo (<c>-f decklink -i …</c>),
///   como siempre; lo que FFmpeg cuenta al abrir llega por <see cref="ReportDetectedMode"/>/<see cref="ReportDeviceOpen"/>.
///   Queda como salida de emergencia (<c>Capture:DecklinkRelay=false</c>).</item>
/// </list>
/// </summary>
public sealed class DecklinkCaptureSource : ICaptureSource
{
    // Audio pedido a la tarjeta (2/8/16 o «auto») y pares elegidos, según los parámetros de la entrada.
    private readonly AudioSelection _audio;
    // Canales con los que se abre AHORA: en «auto» empieza en 16 y baja si la tarjeta no puede (TryReduceAudioChannels).
    private int _channels;
    private readonly Func<DecklinkCaptureSource, IRawCaptureReceiver>? _receiverFactory;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private IRawCaptureReceiver? _receiver;
    private RelayReservation? _reservation;
    private DetectedVideoMode? _detected;          // lo que la tarjeta detectó (o el modo pedido, con modo fijo)
    private FfmpegInputDescription? _description; // lo que FFmpeg contó al abrir el dispositivo
    private bool _deviceOpen;
    private bool _inputSignal = true;              // con la tarjeta abierta: ¿hay señal en la entrada?
    private string? _waitingReason;                // clave del motivo del SIN SEÑAL vigente (relé)
    private bool _audioRejectLogged;

    /// <param name="definition">La entrada guardada (dispositivo, modo, canales de audio).</param>
    /// <param name="receiverFactory">Con él, la fuente trabaja en modo relé (crea el receptor permanente al abrirse);
    /// sin él, captura directa.</param>
    public DecklinkCaptureSource(InputSource definition, Func<DecklinkCaptureSource, IRawCaptureReceiver>? receiverFactory = null, ILogger? log = null)
    {
        Definition = definition;
        _audio = AudioSelection.FromParameters(definition.Parameters);
        _channels = _audio.InitialChannels;
        _receiverFactory = receiverFactory;
        _log = log ?? NullLogger.Instance;
    }

    public InputSource Definition { get; }
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

    /// <summary>Selección de audio de esta entrada (canales pedidos y pares a grabar).</summary>
    public AudioSelection Audio => _audio;

    public int AudioChannelCount => _channels;

    /// <summary>True en modo dispositivo persistente (receptor + relé); false en captura directa.</summary>
    public bool UsesRelay => _receiverFactory is not null;

    /// <summary>¿Hay receptor permanente en marcha? (diagnóstico y tests)</summary>
    public bool IsOpen { get { lock (_sync) return _receiver is not null; } }

    /// <summary>El receptor permanente en marcha (tests).</summary>
    internal IRawCaptureReceiver? Receiver { get { lock (_sync) return _receiver; } }

    public bool TryReduceAudioChannels()
    {
        // Solo en «auto»: con un recuento fijo elegido por el operador no se toca nada (el motor registra el fallo).
        if (!_audio.Auto) return false;
        int next = AudioSelection.NextLower(_channels);
        if (next == 0) return false;
        _channels = next;
        // La señal publicada refleja los canales reales, sin re-emitir SignalChanged (no cambió la presencia).
        if (CurrentSignal.State == SignalState.Locked)
            CurrentSignal = CurrentSignal with
            {
                AudioChannels = _channels,
                AudioSelectionLabel = _channels > 2 ? _audio.Describe(_channels) : null,
                AudioSelectedPairs = _channels > 2 ? _audio.SelectedPairs(_channels) : null,
            };
        return true;
    }

    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (_receiverFactory is null)
        {
            OpenDirect();
            return;
        }

        // Modo relé: arranca el receptor permanente (idempotente: el bucle de espera del motor reabre la fuente cada
        // pocos segundos y NO debe reiniciar un receptor que ya tiene la tarjeta).
        IRawCaptureReceiver receiver;
        SignalInfo signal;
        lock (_sync)
        {
            if (_receiver is not null) return;
            receiver = _receiver = _receiverFactory(this);
            receiver.Opened += OnReceiverOpened;
            receiver.Lost += OnReceiverLost;
            receiver.ModeDetected += OnReceiverModeDetected;
            receiver.DeviceBusy += OnReceiverDeviceBusy;
            receiver.SignalPresence += OnReceiverSignalPresence;
            receiver.AudioChannelsRejected += OnReceiverAudioRejected;
            _deviceOpen = false; _inputSignal = true; _detected = null; _description = null;
            // Sin señal hasta que el receptor abra la tarjeta: con «SEÑAL OK» optimista el operador podría pulsar Grabar
            // sin captura y creer que graba algo.
            signal = CurrentSignal = Waiting("Dl_State_Opening");
        }
        SignalChanged?.Invoke(this, signal);
        try { await receiver.StartAsync(ct).ConfigureAwait(false); }
        catch
        {
            await StopReceiverAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Captura directa: marca LOCK al asignar el dispositivo (igual que DirectShow y el archivo demo), de modo
    /// que el canal habilite el botón Grabar. Lo que FFmpeg cuente al abrir la tarjeta lo trae el motor
    /// (<see cref="ReportDetectedMode"/>/<see cref="ReportDeviceOpen"/>); la pérdida de señal en caliente solo la capta el
    /// watchdog (negros/congelados → carta de ajuste). (Auditoría 24/7, #33.)</summary>
    private void OpenDirect()
    {
        Definition.Parameters.TryGetValue("format_label", out var label);
        CurrentSignal = new SignalInfo(SignalState.Locked,
            Definition.ExpectedResolution, Definition.ExpectedFrameRate,
            Definition.ExpectedAudioLayout, HasAudio: true, Timecode: null, Bitrate: null,
            FormatLabel: string.IsNullOrWhiteSpace(label) ? null : label,
            AudioChannels: _channels,
            AudioSelectionLabel: _channels > 2 ? _audio.Describe(_channels) : null,
            AudioSelectedPairs: _channels > 2 ? _audio.SelectedPairs(_channels) : null);
        SignalChanged?.Invoke(this, CurrentSignal);
    }

    public Task CloseAsync(CancellationToken ct = default) => StopReceiverAsync();

    public async ValueTask DisposeAsync() => await StopReceiverAsync().ConfigureAwait(false);

    private async Task StopReceiverAsync()
    {
        IRawCaptureReceiver? receiver;
        lock (_sync)
        {
            receiver = _receiver;
            _receiver = null;
            _reservation = null;
            if (receiver is not null)
            {
                receiver.Opened -= OnReceiverOpened;
                receiver.Lost -= OnReceiverLost;
                receiver.ModeDetected -= OnReceiverModeDetected;
                receiver.DeviceBusy -= OnReceiverDeviceBusy;
                receiver.SignalPresence -= OnReceiverSignalPresence;
                receiver.AudioChannelsRejected -= OnReceiverAudioRejected;
                CurrentSignal = SignalInfo.None; // sin avisar: al cerrar (reasignación/apagado) ya nadie escucha esta fuente
            }
        }
        if (receiver is not null) await receiver.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Entrada del proceso del canal. Captura directa: el dispositivo (<c>-f decklink … -i "…"</c>). Modo relé: el NUT
    /// que sirve el relé del receptor, solo con la tarjeta abierta y con señal (si no, lanza: el motor entra en espera
    /// y levanta el pipeline cuando la señal llegue); reserva el consumidor para que <see cref="NewestConsumerIsLive"/>
    /// hable de ESTE proceso.
    /// </summary>
    public IReadOnlyList<string> BuildInputArguments()
    {
        if (_receiverFactory is null) return BuildDeviceArguments();
        var receiver = RequireLiveReceiver();
        _reservation = receiver.ReserveConsumer();
        return ConsumerArgumentsFor(receiver.ConsumerPort);
    }

    /// <summary>Sondeo de recuperación: en modo relé, el NUT del relé SIN reservar consumidor y solo si el receptor ve
    /// señal (si no, lanza y el sondeo cuenta como fallido, en vez de dar por buena una tarjeta abierta sin señal).</summary>
    public IReadOnlyList<string> BuildProbeArguments()
        => _receiverFactory is null ? BuildDeviceArguments() : ConsumerArgumentsFor(RequireLiveReceiver().ConsumerPort);

    private IRawCaptureReceiver RequireLiveReceiver()
    {
        IRawCaptureReceiver? receiver;
        SignalInfo signal;
        lock (_sync) { receiver = _receiver; signal = CurrentSignal; }
        if (receiver is null)
            throw new InvalidOperationException(Localizer.F("Dl_Err_CannotOpen", Definition.Name, Localizer.T("Dl_State_Opening")));
        if (signal.State != SignalState.Locked)
            throw new InvalidOperationException(Localizer.F("Dl_Err_CannotOpen", Definition.Name, signal.FormatLabel ?? Localizer.T("Ch_Signal_None")));
        return receiver;
    }

    /// <summary>
    /// Argumentos de entrada del DISPOSITIVO (<c>-f decklink -draw_bars false [-format_code X] -channels N -i "…"</c>): lo
    /// que abre la tarjeta, sea el proceso del canal (captura directa) o el receptor permanente (relé). Se evalúa en cada
    /// lanzamiento: un relanzamiento tras bajar los canales de audio los lee de nuevo.
    /// </summary>
    public IReadOnlyList<string> BuildDeviceArguments()
    {
        var args = new List<string> { "-f", "decklink" };
        // draw_bars=false: el demuxer decklink DIBUJA barras SMPTE por defecto mientras no hay una señal válida —incluida
        // la resincronización al ABRIR el dispositivo—, que se grababan al inicio del archivo. Al desactivarlo la
        // grabación empieza en el primer frame real; la pérdida de señal en caliente la cubren las líneas «No input
        // signal detected» (relé) y el watchdog del motor, no estas barras crudas. (#33.)
        args.AddRange(new[] { "-draw_bars", "false" });
        // Modo de vídeo elegido (si no, autodetección).
        if (Definition.Parameters.TryGetValue("format_code", out var fmt) && !string.IsNullOrWhiteSpace(fmt))
            args.AddRange(new[] { "-format_code", fmt });
        // Canales de audio que se piden a la tarjeta (2, 8 o 16: FFmpeg no admite otros valores). Sin esta opción el
        // demuxer captura SOLO 2 —el par 1-2 del SDI— y el resto del audio embebido se pierde en silencio.
        args.AddRange(new[] { "-channels", _channels.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", Definition.Uri ?? Definition.Name });
        return args;
    }

    /// <summary>Puro y testeable: la entrada NUT por TCP loopback. El NUT trae cabeceras con todos los parámetros de las
    /// pistas (vídeo en crudo y PCM), así que el análisis de entrada es corto: 0,2 s y 20 MB (unos frames de 1080p) bastan
    /// para que el proceso nuevo pinte y grabe enseguida.</summary>
    public static IReadOnlyList<string> ConsumerArgumentsFor(int consumerPort) => new[]
    {
        "-f", "nut", "-analyzeduration", "200000", "-probesize", "20000000",
        "-i", $"tcp://127.0.0.1:{consumerPort}",
    };

    /// <summary>El relé reparte el flujo a varios consumidores: el motor puede arrancar el proceso nuevo antes de retirar el
    /// viejo (relevo sin corte). En captura directa la tarjeta es exclusiva.</summary>
    public bool SupportsOverlappingProcesses => _receiverFactory is not null;

    /// <summary>¿El proceso del último <see cref="BuildInputArguments"/> ya lee el flujo en directo? (relé)</summary>
    public bool NewestConsumerIsLive => _reservation?.IsLive ?? true;

    /// <summary>El relé en crudo no entrega pre-roll: nada que saltarse.</summary>
    public int PreviewFramesToSkip => _reservation?.VideoFrames ?? 0;

    /// <summary>En modo relé el proceso del canal puede quedarse esperando flujo (la tarjeta reabriéndose) sin producir
    /// nada: no es un proceso colgado.</summary>
    public bool WaitsForPeer => _receiverFactory is not null;

    /// <summary>En modo relé, cuando el receptor se relanza el relé cierra al proceso del canal (sale con 0): en preview hay
    /// que relanzarlo para que vuelva a leer el flujo siguiente.</summary>
    public bool RestartsAfterEndOfStream => _receiverFactory is not null;

    /// <summary>Bytes repartidos por el relé al proceso del canal desde que se abrió la fuente (diagnóstico y tests).</summary>
    public long RelayForwardedBytes { get { lock (_sync) return _receiver?.ForwardedBytes ?? 0; } }

    /// <summary>Bytes que el relé recibió del receptor (diagnóstico).</summary>
    public long RelaySourceBytes { get { lock (_sync) return _receiver?.SourceBytes ?? 0; } }

    /// <summary>En qué está el bucle del relé (diagnóstico).</summary>
    public string RelayPumpStage { get { lock (_sync) return _receiver?.PumpStage ?? ""; } }

    /// <summary>True si la entrada deja que la tarjeta detecte el modo (sin <c>format_code</c>).</summary>
    private bool Autodetects => !Definition.Parameters.TryGetValue("format_code", out var code) || string.IsNullOrWhiteSpace(code);

    /// <summary>
    /// Captura directa: lo que FFmpeg contó al abrir la tarjeta (lo trae el motor desde su stderr). En autodetección el
    /// modo detectado pasa a la señal y un fallo la deja en SIN SEÑAL hasta que la tarjeta detecte algo; con modo FIJO
    /// se ignora. En modo relé se ignora siempre: lo cuenta el receptor.
    /// </summary>
    public void ReportDetectedMode(DetectedVideoMode? mode)
    {
        if (_receiverFactory is not null || !Autodetects) return;
        var current = CurrentSignal;
        var next = mode is null
            ? current with { State = SignalState.NoSignal, Resolution = null, FrameRate = null, FormatLabel = null }
            : current with { State = SignalState.Locked, Resolution = mode.Resolution, FrameRate = mode.FrameRate, FormatLabel = mode.Label };
        if (next.State == current.State && next.FormatLabel == current.FormatLabel) return;
        RaiseSignal(next);
    }

    /// <summary>Captura directa: FFmpeg abrió el dispositivo (hay captura) o la tarjeta estaba en uso por otro proceso.
    /// En modo relé se ignora: lo que el proceso del canal cuente de SU entrada (el relé) no habla de la tarjeta.</summary>
    public void ReportDeviceOpen(bool opened)
    {
        if (_receiverFactory is not null) return;
        var current = CurrentSignal;
        var state = opened ? SignalState.Locked : SignalState.NoSignal;
        if (current.State == state) return;
        RaiseSignal(current with { State = state });
    }

    /// <summary>Propaga un cambio de señal (presencia/ausencia/formato).</summary>
    internal void RaiseSignal(SignalInfo info)
    {
        CurrentSignal = info;
        SignalChanged?.Invoke(this, info);
    }

    // --- Modo relé: la señal la dicta el receptor permanente ---

    /// <summary>Señal presente con lo que sirve el receptor: el modo detectado por la tarjeta (autodetección) o el
    /// pedido (modo fijo; con su etiqueta guardada), resolución/tasa reales y los canales de audio abiertos.</summary>
    private SignalInfo LockedSignal()
    {
        var mode = _detected ?? _description?.Video;
        Definition.Parameters.TryGetValue("format_label", out var label);
        string? formatLabel = !Autodetects && !string.IsNullOrWhiteSpace(label) ? label : mode?.Label;
        return new SignalInfo(SignalState.Locked,
            mode?.Resolution ?? Definition.ExpectedResolution, mode?.FrameRate ?? Definition.ExpectedFrameRate,
            Definition.ExpectedAudioLayout, HasAudio: _description?.HasAudio ?? true, Timecode: null, Bitrate: null,
            FormatLabel: formatLabel,
            AudioChannels: _channels,
            AudioSelectionLabel: _channels > 2 ? _audio.Describe(_channels) : null,
            AudioSelectedPairs: _channels > 2 ? _audio.SelectedPairs(_channels) : null);
    }

    /// <summary>Sin señal, con el motivo en palabras (abriendo, en uso, sin detección, reabriendo).</summary>
    private SignalInfo Waiting(string reasonKey)
    {
        _waitingReason = reasonKey;
        return new SignalInfo(SignalState.NoSignal, null, null, Definition.ExpectedAudioLayout, HasAudio: true,
            Timecode: null, Bitrate: null, FormatLabel: Localizer.T(reasonKey), AudioChannels: _channels);
    }

    private void Publish(SignalInfo? signal)
    {
        if (signal is null) return;
        SignalChanged?.Invoke(this, signal);
    }

    private void OnReceiverOpened(object? sender, FfmpegInputDescription description)
    {
        SignalInfo? signal;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _receiver)) return; // un receptor ya cerrado
            _description = description;
            _deviceOpen = true;
            _inputSignal = true; // hasta que la tarjeta diga lo contrario
            _waitingReason = null;
            signal = CurrentSignal = LockedSignal();
        }
        _log.LogInformation("DeckLink «{Name}»: tarjeta abierta por el receptor permanente ({Format}, {Channels} canales de audio).",
            Definition.Name, signal.FormatLabel ?? "formato sin describir", _channels);
        Publish(signal);
    }

    private void OnReceiverModeDetected(object? sender, DetectedVideoMode? mode)
    {
        SignalInfo? signal = null;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _receiver)) return;
            if (mode is null)
            {
                // Autodetección sin resultado: FFmpeg sale y el receptor reintenta; mientras, sin señal.
                _detected = null;
                if (Autodetects && _waitingReason != "Dl_State_NoDetect") signal = CurrentSignal = Waiting("Dl_State_NoDetect");
            }
            else
            {
                _detected = mode;
                // Normalmente llega ANTES del volcado de la entrada (Opened publica el modo); si llega después, se republica.
                if (_deviceOpen && _inputSignal && CurrentSignal.FormatLabel != LockedSignal().FormatLabel)
                    signal = CurrentSignal = LockedSignal();
            }
        }
        Publish(signal);
    }

    private void OnReceiverDeviceBusy(object? sender, EventArgs e)
    {
        SignalInfo? signal = null;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _receiver)) return;
            _deviceOpen = false;
            if (_waitingReason != "Dl_State_Busy") signal = CurrentSignal = Waiting("Dl_State_Busy");
        }
        if (signal is not null)
            _log.LogWarning("DeckLink «{Name}»: la tarjeta está en uso por otro proceso (otro programa, u otro canal con la misma entrada); se reintenta hasta que quede libre.", Definition.Name);
        Publish(signal);
    }

    private void OnReceiverSignalPresence(object? sender, bool present)
    {
        SignalInfo? signal = null;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _receiver)) return;
            _inputSignal = present;
            if (!_deviceOpen) return;
            var wanted = present ? SignalState.Locked : SignalState.NoSignal;
            if (CurrentSignal.State == wanted) return;
            // Sin señal en la entrada, pero con la tarjeta abierta: se conserva el formato con el que captura.
            signal = CurrentSignal = present ? LockedSignal() : LockedSignal() with { State = SignalState.NoSignal };
            _waitingReason = null;
        }
        _log.LogInformation("DeckLink «{Name}»: {What}.", Definition.Name, present ? "la señal volvió a la entrada" : "la entrada se quedó sin señal");
        Publish(signal);
    }

    private void OnReceiverLost(object? sender, EventArgs e)
    {
        SignalInfo? signal = null;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _receiver)) return;
            _deviceOpen = false;
            _description = null;
            // Si ya se sabe POR QUÉ no abre (en uso, sin detección), se mantiene ese motivo; si no, «reabriendo».
            if (_waitingReason is "Dl_State_Busy" or "Dl_State_NoDetect") return;
            signal = CurrentSignal = Waiting("Dl_State_Reopening");
        }
        Publish(signal);
    }

    private void OnReceiverAudioRejected(object? sender, EventArgs e)
    {
        IRawCaptureReceiver? receiver;
        lock (_sync) receiver = ReferenceEquals(sender, _receiver) ? _receiver : null;
        if (receiver is null) return;
        int before = _channels;
        if (TryReduceAudioChannels())
        {
            _log.LogWarning("DeckLink «{Name}»: la tarjeta rechazó {Before} canales de audio; se reabre con {After}.", Definition.Name, before, _channels);
            _ = Task.Run(async () =>
            {
                try { await receiver.RestartAsync().ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "DeckLink «{Name}»: no se pudo reabrir la tarjeta con {Channels} canales.", Definition.Name, _channels); }
            });
        }
        else if (!_audioRejectLogged)
        {
            _audioRejectLogged = true;
            _log.LogError("DeckLink «{Name}»: la tarjeta no admite {N} canales de audio y la entrada no permite bajar; elige en «Entradas» un valor que la tarjeta admita (2 u 8).", Definition.Name, before);
        }
    }
}

/// <summary>Fábrica que registra el soporte DeckLink en el sistema de captura. Con FFmpeg y el relé activado
/// (<c>Capture:DecklinkRelay</c>, por defecto), cada fuente trabaja con un receptor permanente; si no, captura directa.</summary>
public sealed class DecklinkCaptureSourceFactory(IFfmpegLocator? ffmpeg = null, ILoggerFactory? loggers = null, bool relay = true) : ICaptureSourceFactory
{
    public bool CanHandle(InputType type) => type is InputType.DecklinkSdi;

    public ICaptureSource Create(InputSource definition)
    {
        if (!relay || ffmpeg is null) return new DecklinkCaptureSource(definition);
        var factory = loggers ?? NullLoggerFactory.Instance;
        return new DecklinkCaptureSource(definition,
            src => new RawCaptureReceiver(definition.Name, src.BuildDeviceArguments, copyStreams: true, ffmpeg.FfmpegPath, factory.CreateLogger<RawCaptureReceiver>()),
            factory.CreateLogger<DecklinkCaptureSource>());
    }
}
