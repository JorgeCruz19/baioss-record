using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Motor de captura UNIFICADO de un canal: un único proceso FFmpeg abre la fuente UNA sola vez y la
/// bifurca en preview (frames BGRA por TCP loopback) + medidores (ebur128) y, al grabar, también la
/// salida a archivo — todo a la vez. Así un dispositivo en vivo (DeckLink/cámara), que no admite dos
/// aperturas, puede previsualizarse y grabarse simultáneamente. Reutiliza el
/// <see cref="FfmpegProcessSupervisor"/> (watchdog/respawn 24/7), el <see cref="FfmpegProgressParser"/>
/// (telemetría) y el <see cref="FfmpegArgumentBuilder"/>. Alternar grabación reconstruye el argv y
/// reinicia el proceso (breve reconexión del preview); el cierre ordenado del supervisor finaliza el
/// archivo correctamente.
/// </summary>
public sealed class FfmpegChannelEngine : IChannelPreviewSource, IAsyncDisposable
{
    private readonly IFfmpegLocator _locator;
    private readonly ILogger _log;
    private readonly FfmpegProgressParser _parser = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Colchón de preview (ver PreviewPacer): los frames del sumidero con el mando se entregan a través de él; sin colchón
    // (fuentes locales, o de red sin configurarlo) es paso directo. Uno por canal, POR ENCIMA de los sumideros: un relevo
    // de proceso ni lo vacía ni lo vuelve a llenar. Su pool de búferes sustituye al anillo de 3 que había por lector.
    private PreviewPacer? _pacer;
    private PreviewPacer Pacer => _pacer ??= new PreviewPacer(FrameWidth * 4 * FrameHeight, DeliverFrame, _channelKey);
    private void DeliverFrame(byte[] bgra) => FrameReady?.Invoke(this, new PreviewFrame(bgra, FrameWidth, FrameHeight, FrameWidth * 4));

    /// <summary>Estado del colchón de preview (diagnóstico y tests): frames en cola, entregados, repetidos (huecos) y descartados (ráfagas).</summary>
    public (int Queued, long Delivered, long Repeated, long Dropped) PreviewCushion =>
        _pacer is { } p ? (p.QueuedFrames, p.Delivered, p.Repeated, p.Dropped) : (0, 0, 0, 0);

    private ICaptureSource? _source;
    private RecordingProfile? _baseProfile;
    private RecordingProfile? _recordProfile;
    private string _channelKey = "A";

    private FfmpegProcessSupervisor? _supervisor;
    // Sumidero de preview del proceso ACTUAL. Uno por proceso: al reemplazarlo (Grabar/Detener) el nuevo arranca sobre el
    // suyo mientras el viejo sigue pintando en el anterior, y el relevo se hace en el primer frame del nuevo (ver
    // ReplaceProcessAsync / HandoffAsync). Solo pinta el sumidero de generación más alta que ya entregó un frame.
    private PreviewSink? _sink;
    private int _sinkGeneration;
    private volatile int _activeSink;
    // Sumideros vivos (el actual y los que esperan su relevo): cuando uno toma el mando, los más viejos que aún no habían
    // pintado quedan relevados (su HandoffAsync retira su proceso sin esperar más).
    private readonly List<PreviewSink> _liveSinks = new();
    /// <summary>Tope de espera a que el proceso nuevo tome el mando del preview antes de retirar el viejo de todos modos.
    /// Generoso: mientras tanto el viejo sigue pintando el directo, y un proceso que graba a 1080p con un preset lento tarda
    /// varios segundos en digerir el pre-roll (grabación) antes de ir en directo.</summary>
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(15);

    private RecordingState _state = RecordingState.Idle;
    private Guid _sessionId;
    private int _segmentIndex;
    private string? _recordFile;
    private DateTimeOffset _recordStart;

    // Bitrate REAL de grabación, medido por el CRECIMIENTO del archivo en disco. El bitrate del parser es el
    // agregado de FFmpeg, dominado por el preview CRUDO (rawvideo a un socket loopback, ~180 Mbps a 360p) que
    // NO es el archivo; durante la grabación se sustituye por este para que la UI muestre el bitrate real. Se
    // siembra con el objetivo del perfil y converge (EMA) al medido.
    private string? _recordDir;
    private long _lastRecBytes;
    private long _recordedBytes; // bytes de la sesión en curso (última medida); se expone en RecorderStats. (N16.)
    private DateTimeOffset _lastRecSampleUtc;
    private long _realBitrateBps;

    // Nombre base del archivo (sin extensión) elegido por el operador (manual) o derivado de la
    // programación (dd-MM-yyyy_Título). null → nombre por defecto {canal}_{fecha_hora}.
    private string? _recordBaseName;

    // Segmentación: vigilancia del directorio de segmentos (cada archivo completo → un Segment).
    private bool _segmented;
    private string _segDir = "";
    private string _segGlob = "";
    private readonly HashSet<string> _emitted = new(StringComparer.OrdinalIgnoreCase);
    // Archivos REALMENTE escritos en esta sesión (en orden de emisión), para renombrarlos al detener una
    // grabación manual (cuyo nombre se pide al final, no al iniciar).
    private readonly List<string> _sessionFiles = new();
    // Snapshot de los archivos de la ÚLTIMA sesión terminada, tomado AL DETENER: el renombrado manual (que se
    // pide en un diálogo que puede tardar) opera sobre ESTE, no sobre `_sessionFiles`, que una grabación nueva
    // (p. ej. una programada que arranca mientras el diálogo está abierto) habría vaciado → si no, se renombrarían
    // los archivos de la sesión NUEVA. Además desacopla del hilo del escaneo de segmentos. (Auditoría N9.)
    private readonly List<string> _completedSessionFiles = new();
    // Optimizaciones de seek (remux faststart) EN VUELO. El renombrado debe esperar a TODAS: el remux reescribe
    // el archivo in-place y no debe solaparse con el File.Move del rename (carrera → duplicado/huérfano). Una
    // sesión con recuperación de caídas (N1) produce VARIAS piezas, cada una con su remux; guardar solo la última
    // dejaba escapar las anteriores. Lista bajo lock (se emite desde el hilo del escaneo y el del progreso). (N29.)
    private readonly List<Task> _pendingOptimizes = new();
    private readonly object _optimizeLock = new();
    private CancellationTokenSource? _segScanCts;
    private Task? _segScanLoop;

    // Carta de ajuste (slate) ante pérdida de señal + sondeo de recuperación del dispositivo.
    private volatile bool _slate;
    private volatile bool _slatePending;
    private CancellationTokenSource? _recoveryCts;
    private Task? _recoveryLoop;
    // Espera de señal INICIAL: si la fuente no tiene señal al arrancar (p. ej. NDI cuyo emisor aún no emite), el
    // pipeline no puede construirse; este bucle reintenta abrir la fuente y levanta el preview en cuanto llega.
    private CancellationTokenSource? _awaitCts;
    private Task? _awaitLoop;
    private DateTimeOffset _slateSince;       // cuándo entró en slate, para escalar a alarma si se prolonga
    private volatile bool _slateAlarmRaised;  // ya se elevó SignalLoss por slate prolongado (dedupe)

    // Alarmas activas (dedupe: solo se notifican transiciones).
    private readonly HashSet<AlarmType> _activeAlarms = new();
    private volatile bool _disposed;

    // Fallback de codificador: si el codificador de vídeo de grabación no ABRE (NVENC agotado, driver/GPU
    // ausente), el canal degrada al siguiente de la cadena (QSV→AMF→CPU) y reinicia el proceso, en lugar de
    // reintentar en vano el mismo. _encoderOpenError: se vio el fallo en el stderr del proceso ACTUAL.
    // _fallbackPending: hay una degradación en curso (evita disparos repetidos y bloquea el slate, que
    // reusaría el mismo codificador roto).
    private volatile bool _encoderOpenError;
    private volatile bool _fallbackPending;

    // Retroceso de canales de audio (DeckLink con «audio_channels=auto»): la tarjeta rechazó los canales pedidos
    // («Cannot enable audio input») → la fuente baja un escalón (16→8→2) y se reconstruye el proceso con el argv nuevo.
    private volatile bool _audioFallbackPending;
    /// <summary>Fuente para la que ya se registró que rechaza el recuento FIJO de canales (evita repetir el error en cada relanzamiento).</summary>
    private ICaptureSource? _audioRejectLoggedFor;
    // Fuente para la que ya se avisó de que la tarjeta no autodetecta la señal (el supervisor relanza y la línea vuelve).
    private ICaptureSource? _autodetectLoggedFor;
    // Ídem para «la tarjeta está en uso por otro proceso».
    private ICaptureSource? _deviceBusyLoggedFor;

    // Recuperación tras la CAÍDA del proceso de grabación (N1): el supervisor ya NO relanza el mismo argv —que
    // reabriría el archivo con -y y lo truncaría—; el motor reconstruye en una PIEZA NUEVA vía ReplaceProcessAsync,
    // con backoff exponencial acotado que se resetea tras un periodo sano. _recovering evita recuperaciones solapadas.
    private volatile bool _recovering;
    private int _recordRestartCount;
    private DateTimeOffset _lastRecordDeathUtc;

    public FfmpegChannelEngine(IFfmpegLocator locator, ILogger log)
    {
        _locator = locator;
        _log = log;
    }

    /// <summary>Tras este tiempo en carta de ajuste SIN recuperar la señal, se eleva una alarma crítica (SignalLoss) para que el operador actúe; la grabación de barras NO se detiene (por si la señal vuelve).</summary>
    public TimeSpan SlateAlarmAfter { get; init; } = TimeSpan.FromMinutes(5);

    public int FrameWidth { get; init; } = 640;
    public int FrameHeight { get; init; } = 360;

    /// <summary>Raíz de grabación; se crea una subcarpeta por canal.</summary>
    public string OutputRoot { get; set; } = "recordings";

    /// <summary>Modo de contenedor MP4/MOV: <c>true</c> (por defecto) fMP4 fragmentado robusto ante corte
    /// (+ remux a faststart del archivo único al detener); <c>false</c> MP4 estándar con moov al final (seekable
    /// en local, sin remux ni saturación de disco; requiere cierre limpio → para máquinas con SAI/UPS).
    /// Configurable en «Recording:FragmentedMp4».</summary>
    public bool FragmentedMp4 { get; init; } = true;

    public RecordingState State => _state;
    public RecorderStats Stats { get; private set; } = RecorderStats.Empty;
    public string? LastOutputFile { get; private set; }

    public event EventHandler<PreviewFrame>? FrameReady;
    public event EventHandler<IReadOnlyList<double>>? AudioPeaksUpdated;
    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<RecorderStats>? StatsUpdated;
    public event EventHandler<Segment>? SegmentClosed;

    /// <summary>Transición de alarma del canal (negro/congelado/silencio/slate): tipo y si pasa a activa.</summary>
    public event EventHandler<(AlarmType Type, bool Active)>? AlarmChanged;

    /// <summary>El proceso de grabación murió a mitad y se va a recuperar en una pieza nueva: código de salida
    /// de FFmpeg y lo que se sabe del motivo. Para la AUDITORÍA: es lo que explica un archivo cortado.</summary>
    public event EventHandler<(int ExitCode, string Reason)>? RecordingInterrupted;

    /// <summary>Un archivo recién cerrado no pasó la verificación (sin pistas/duración legibles): ruta y tamaño.
    /// Para la AUDITORÍA, además de la alarma en pantalla.</summary>
    public event EventHandler<(string FilePath, long SizeBytes)>? FileUnverified;

    /// <summary>True mientras el canal rellena con carta de ajuste por pérdida de señal.</summary>
    public bool IsSlate => _slate;

    /// <summary>Arranca la captura SIEMPRE-ACTIVA (preview + medidores) sobre la fuente del canal.</summary>
    public async Task StartPreviewAsync(ICaptureSource source, RecordingProfile baseProfile, string channelKey, CancellationToken ct = default)
    {
        _source = source;
        _baseProfile = baseProfile;
        _channelKey = channelKey;
        // La fuente puede reportar pérdida/recuperación de señal en caliente (NDI ahora también): reacciona
        // entrando/saliendo de carta de ajuste sin esperar al watchdog de 15 s. (Auditoría 24/7, C3.)
        source.SignalChanged += OnSourceSignalChanged;

        // El servidor TCP loopback de los frames de preview se crea con cada proceso (ver ReplaceProcessAsync).

        // Si la fuente todavía NO tiene señal (caso típico de NDI cuyo emisor aún no emite), el pipeline no se
        // puede construir (BuildInputArguments lanza sin receptor). En vez de fallar el arranque del canal, se
        // entra en ESPERA y un bucle reintenta abrir la fuente; el preview se levanta solo en cuanto llega señal.
        if (!await TryStartPreviewPipelineAsync(ct).ConfigureAwait(false))
        {
            _log.LogInformation("Canal {Key}: la fuente no tiene señal todavía; esperando para activar el preview.", _channelKey);
            StartAwaitSignalProbe();
        }
    }

    /// <summary>Intenta arrancar el pipeline de preview; devuelve false si la fuente aún no permite construirlo (sin señal).</summary>
    private async Task<bool> TryStartPreviewPipelineAsync(CancellationToken ct)
    {
        try
        {
            await ReplaceProcessAsync(recording: false, slate: false, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Canal {Key}: aún no se puede construir el pipeline (fuente sin señal).", _channelKey);
            return false;
        }
    }

    private void StartAwaitSignalProbe()
    {
        _awaitCts?.Dispose(); // dispone el de la espera anterior antes de reabrir: sin fuga de CTS. (#53)
        _awaitCts = new CancellationTokenSource();
        _awaitLoop = Task.Run(() => AwaitSignalLoopAsync(_awaitCts.Token));
    }

    /// <summary>
    /// Bucle de espera de la señal INICIAL: reintenta abrir la fuente cada pocos segundos (en NDI, reconecta el
    /// receptor) y, en cuanto reporta <see cref="SignalState.Locked"/>, levanta el pipeline de preview y termina.
    /// Así un canal cuya fuente no emitía al arrancar se recupera SOLO, sin reiniciar la app.
    /// </summary>
    private async Task AwaitSignalLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try { await _source!.OpenAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Canal {Key}: reintento de apertura de la fuente falló.", _channelKey); continue; }

            if (_source!.CurrentSignal.State != SignalState.Locked || ct.IsCancellationRequested) continue;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!ct.IsCancellationRequested && _state == RecordingState.Idle &&
                    await TryStartPreviewPipelineAsync(ct).ConfigureAwait(false))
                {
                    _log.LogInformation("Canal {Key}: señal detectada; preview activo.", _channelKey);
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            finally { _gate.Release(); }
        }
    }

    private void StopAwaitSignalProbe()
    {
        try { _awaitCts?.Cancel(); } catch { /* dispuesto */ }
    }

    /// <summary>Arranca la grabación a archivo SIN interrumpir el preview (mismo proceso, salida extra).</summary>
    public async Task StartRecordingAsync(Guid sessionId, RecordingProfile profile, string? baseName = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _recordProfile = profile;
            _sessionId = sessionId;
            _segmentIndex = 0;
            _recordFile = null;
            _emitted.Clear();
            _sessionFiles.Clear();
            lock (_optimizeLock) _pendingOptimizes.Clear(); // remux de la sesión anterior ya esperados en su rename (N29)
            _slate = false; _slatePending = false;
            _encoderOpenError = false; _fallbackPending = false;
            _recovering = false; _recordRestartCount = 0; // backoff de recuperación limpio para esta grabación (N1)
            RaiseAlarm(AlarmType.EncoderFallback, false);     // limpia un fallback de una sesión previa
            RaiseAlarm(AlarmType.RecordingUnverified, false); // y el aviso de archivo dañado de la anterior
            _recordStart = DateTimeOffset.UtcNow;
            // Medición del bitrate REAL por crecimiento del archivo, sembrada con el objetivo del perfil (así la
            // UI nunca muestra el bitrate inflado del preview crudo del pipeline unificado).
            _recordDir = OutputRoot; // sin subcarpeta por canal: se graba directo en la carpeta destino configurada
            _lastRecBytes = 0;
            _recordedBytes = 0;
            _lastRecSampleUtc = _recordStart;
            _realBitrateBps = profile.VideoBitrate.BitsPerSecond + profile.AudioBitrate.BitsPerSecond;
            // Nombre base elegido (manual) o derivado de la programación; null → {canal}_{fecha_hora}.
            _recordBaseName = SanitizeBaseName(baseName);

            // ¿Segmentado? Prepara la vigilancia del directorio (cada archivo completo → un Segment) y
            // siembra los archivos previos para no re-emitir grabaciones anteriores (los del MISMO nombre
            // base, p. ej. al reanudar tras un reinicio: no se re-emiten, pero sí cuentan para continuar
            // la numeración de segmentos sin sobrescribirlos).
            _segmented = profile.Segmentation is { Trigger: SegmentTrigger.Duration or SegmentTrigger.Size or SegmentTrigger.WallClock };
            if (_segmented)
            {
                var (_, ext) = FfmpegCodecMap.Container(profile.Container);
                _segDir = OutputRoot; // sin subcarpeta por canal
                _segGlob = $"{_recordBaseName ?? _channelKey}_*.{ext}";
                Directory.CreateDirectory(_segDir);
                foreach (var f in Directory.GetFiles(_segDir, _segGlob)) _emitted.Add(f);
            }

            SetState(RecordingState.Starting);
            try
            {
                await ReplaceProcessAsync(recording: true, slate: false, ct).ConfigureAwait(false);
            }
            catch
            {
                // La grabación NO arrancó: revierte a un estado coherente en vez de dejar el canal atascado en
                // «Starting» —donde el guard anti-doble-START lo bloquearía para siempre y el scheduler lo
                // reintentaría cada segundo—. Restaura el preview y relanza para avisar al llamador. (Auditoría N3.)
                _recordProfile = null;
                _segmented = false;
                _recordFile = null;
                _recordDir = null;
                await RestorePreviewAfterFailureAsync().ConfigureAwait(false);
                SetState(RecordingState.Idle);
                throw;
            }

            if (_segmented)
            {
                _segScanCts = new CancellationTokenSource();
                _segScanLoop = Task.Run(() => SegmentScanLoopAsync(_segScanCts.Token));
            }
            SetState(RecordingState.Recording);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Detiene la grabación y vuelve a preview-only; el cierre ordenado finaliza el archivo.</summary>
    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        StopRecoveryProbe();
        await StopSegmentScanAsync().ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetState(RecordingState.Stopping);
            _slate = false; _slatePending = false;
            try
            {
                await ReplaceProcessAsync(recording: false, slate: false, ct).ConfigureAwait(false); // dispone el de grabación → flush/moov (y emite el archivo único)
                if (_segmented) ScanSegments(includeNewest: true); // emite los segmentos restantes, incluido el último ya finalizado
            }
            catch (Exception ex)
            {
                // La grabación YA quedó finalizada (ReplaceProcessAsync dispone el proceso de grabación PRIMERO →
                // flush/moov del archivo); lo que falló es rearmar el preview. Completa el stop igualmente y
                // restaura el preview best-effort, en vez de dejar el canal atascado en «Stopping». (Auditoría N3.)
                _log.LogError(ex, "Canal {Key}: la grabación se detuvo pero no se pudo rearmar el preview; se restaura.", _channelKey);
                if (_segmented) { try { ScanSegments(includeNewest: true); } catch { /* best-effort */ } }
                await RestorePreviewAfterFailureAsync().ConfigureAwait(false);
            }
            // Snapshot de los archivos de ESTA sesión para el renombrado posterior (inmune a que una grabación
            // nueva vacíe _sessionFiles mientras el operador nombra en el diálogo). (Auditoría N9.)
            lock (_completedSessionFiles)
            {
                _completedSessionFiles.Clear();
                _completedSessionFiles.AddRange(_sessionFiles);
            }
            RaiseAlarm(AlarmType.Slate, false);
            RaiseAlarm(AlarmType.SignalLoss, false);
            RaiseAlarm(AlarmType.EncoderFallback, false);
            _encoderOpenError = false; _fallbackPending = false; _slateAlarmRaised = false;
            _recordProfile = null;
            _segmented = false;
            Stats = RecorderStats.Empty;
            SetState(RecordingState.Idle);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Restaura el pipeline de solo-preview tras un fallo de Start/Stop (donde <see cref="ReplaceProcessAsync"/>
    /// dejó el canal sin proceso). Si la fuente aún no tiene señal para construirlo, entra en ESPERA y el preview
    /// se levanta solo cuando la señal vuelve. NUNCA lanza: es la red de recuperación, no debe volver a romper. (N3.)
    /// </summary>
    private async Task RestorePreviewAfterFailureAsync()
    {
        try
        {
            if (!await TryStartPreviewPipelineAsync(CancellationToken.None).ConfigureAwait(false))
                StartAwaitSignalProbe();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Canal {Key}: no se pudo restaurar el preview tras un fallo; se esperará a la señal.", _channelKey);
            StartAwaitSignalProbe();
        }
    }

    private async Task ReplaceProcessAsync(bool recording, bool slate, CancellationToken ct)
    {
        var previous = _supervisor;
        var previousSink = _sink;
        // Modo archivo único: el proceso saliente deja su archivo finalizado en disco al cerrarse → se emite como segmento
        // (en modo segmentado lo hace el escaneo del directorio, _recordFile es null). Solo en fMP4 se remuxea a faststart
        // para arreglar el seek; en MP4 estándar (moov al final) el archivo YA es seekable. (Config Recording:FragmentedMp4.)
        string? previousFile = _recordFile;
        _recordFile = null;
        // Si la fuente admite dos aperturas (el relé de una entrada de red), el proceso viejo SIGUE PINTANDO mientras
        // el nuevo arranca, y se retira en el primer frame del nuevo (HandoffAsync): el preview no se congela ni un
        // instante al Grabar/Detener. Un dispositivo no lo admite: se retira el viejo antes de abrir el nuevo (su cierre
        // ordenado envía 'q' y finaliza el archivo si grababa).
        // Solo si el viejo sigue EN MARCHA: uno que ya salió (el emisor se fue, el proceso murió) no pinta nada que
        // mantener, y su archivo debe emitirse ya, aunque el nuevo no pueda construirse todavía (sin emisor).
        bool overlap = previous is { IsRunning: true } && _source?.SupportsOverlappingProcesses == true;

        if (previous is not null)
        {
            Detach(previous); // sus eventos ya no cuentan: lo que le pase en el relevo (o al finalizar) no es una caída (N6)
            _supervisor = null; _sink = null; // desde aquí viven en previous/previousSink: se retiran ahora o tras el relevo
            if (!overlap)
            {
                await RetireAsync(previous, previousSink).ConfigureAwait(false);
                if (previousFile is not null) { EmitSegmentFile(previousFile, optimizeSeek: FragmentedMp4); previousFile = null; }
            }
        }

        // El stream cambió: cualquier negro/congelado/silencio detectado pertenecía al proceso anterior.
        ClearDetectAlarms();

        // El proceso entrante puede llevar un códec distinto (degradado): su capacidad de abrir el
        // codificador se re-evalúa con SU propio stderr, no con el del proceso saliente.
        _encoderOpenError = false;
        // La alarma de disco colgado pertenecía al supervisor saliente; el entrante la volverá a levantar si procede.
        RaiseAlarm(AlarmType.DiskStalled, false);

        PreviewSink sink;
        try { sink = await LaunchProcessAsync(recording, slate, overlap, ct).ConfigureAwait(false); }
        catch when (overlap)
        {
            // El nuevo no arrancó (p. ej. la fuente perdió el emisor entre medias): se retira el viejo y se emite su
            // archivo, como sin solape, antes de propagar; el llamador restaura el preview sobre un estado sin procesos.
            await RetireAsync(previous!, previousSink).ConfigureAwait(false);
            if (previousFile is not null) EmitSegmentFile(previousFile, optimizeSeek: FragmentedMp4);
            throw;
        }

        if (overlap)
        {
            // Relevo: el viejo sigue pintando hasta el primer frame del nuevo. Si el viejo GRABABA, se espera aquí (su
            // archivo debe estar finalizado al volver: Detener lo emite y lo renombra); si solo hacía preview, no hace falta
            // esperar (Grabar responde al instante) y se retira en segundo plano.
            var handoff = HandoffAsync(sink, previous!, previousSink);
            if (previous!.FinalizeOnStop)
            {
                await handoff.ConfigureAwait(false);
                if (previousFile is not null) EmitSegmentFile(previousFile, optimizeSeek: FragmentedMp4);
            }
            else _ = handoff;
        }
    }

    /// <summary>Construye y arranca el proceso FFmpeg del canal sobre un sumidero de preview NUEVO (que devuelve); deja el
    /// supervisor en <see cref="_supervisor"/> y el sumidero en <see cref="_sink"/>.</summary>
    private async Task<PreviewSink> LaunchProcessAsync(bool recording, bool slate, bool overlap, CancellationToken ct)
    {
        var profile = recording ? _recordProfile! : (_baseProfile ?? _recordProfile!);

        // El parser deriva el HH:MM:SS del tiempo real, pero necesita la tasa nominal para los cuadros
        // (FF) y para no asumir 25 fps fijos. Prioridad: tasa de salida del perfil → señal de la fuente.
        _parser.NominalRate = ResolveNominalRate(profile);

        // Carpeta destino TAL CUAL (sin subcarpeta A/B/C/D por canal): el operador configura la carpeta de cada
        // canal, así que anidar la clave del canal era redundante. Se crea si no existe al empezar a grabar.
        var dir = OutputRoot;
        if (recording) Directory.CreateDirectory(dir);

        // Servidor TCP loopback para los frames de preview de ESTE proceso (los relanzamientos internos reconectan a él).
        // Con relevo, no toma el mando hasta que la fuente confirme que ya lee en directo y sus frames lleguen a cadencia
        // real: hasta entonces el proceso decodifica el pre-roll y el atraso acumulado más deprisa que el directo, y
        // pintarlos sería un avance rápido. La cadencia se juzga contra la tasa REAL de la fuente (el preview va en
        // passthrough: pinta cada frame de la entrada, no los de la tasa de salida del perfil).
        var source = _source!;
        double sourceFps = source.CurrentSignal.FrameRate is { Value: > 0 } rate ? rate.Value : _parser.NominalRate;
        var sink = new PreviewSink(++_sinkGeneration)
        {
            SettleBeforeTakeover = overlap,
            NominalIntervalMs = 1000.0 / sourceFps,
            IsLive = () => source.NewestConsumerIsLive,
        };
        lock (_liveSinks) _liveSinks.Add(sink);
        sink.Run((s, token) => AcceptLoopAsync(s, token));
        _sink = sink;
        // Colchón de preview de la fuente (0 = paso directo) a la cadencia real de la señal. Se vuelve a fijar en cada
        // proceso: la señal puede haberse redetectado con otra tasa, o el operador haber cambiado el colchón de la fuente.
        Pacer.Configure(TimeSpan.FromMilliseconds(Math.Max(0, source.PreviewBufferMs)), sourceFps);

        var builder = new FfmpegArgumentBuilder()
            .From(_source!).Using(profile).ForChannel(_channelKey)
            .ToDirectory(dir).WithPreviewSink($"tcp://127.0.0.1:{sink.Port}")
            .WithFragmentedMp4(FragmentedMp4);

        // Nombre del archivo. Si hay un nombre base (manual/programada), lo aplica; si no, el builder usa
        // el de por defecto {canal}_{fecha_hora}. Se resuelve POR proceso para que cada pieza nueva (corte
        // por slate/reinicio) no pise a la anterior.
        if (recording && _recordBaseName is not null)
        {
            var (_, ext) = FfmpegCodecMap.Container(profile.Container);
            if (_segmented)
                // Segmentos «{base}_1, _2…» con numeración CONTINUA: arranca tras los ya existentes.
                builder.WithBaseName(_recordBaseName).WithSegmentStartNumber(NextSegmentNumber(dir, _recordBaseName, ext));
            else
                // Archivo único con nombre ÚNICO: si choca, añade « 1», « 2»… (también entre piezas de slate).
                builder.WithBaseName(ResolveUniqueSingleName(dir, _recordBaseName, ext));
        }

        var args = slate
            ? builder.BuildSlate(recording, FrameWidth, FrameHeight)
            : builder.BuildLive(recording, FrameWidth, FrameHeight);

        if (recording)
        {
            // Solo seguimos el archivo único; en modo segmentado OutputFilePath es vacío y los segmentos
            // los emite el escaneo del directorio.
            _recordFile = builder.IsSegmentedOutput || string.IsNullOrEmpty(builder.OutputFilePath)
                ? null : builder.OutputFilePath;
            if (_recordFile is not null) LastOutputFile = _recordFile;
        }

        _log.LogInformation("Pipeline canal {Key} ({Mode}): {Args}",
            _channelKey, recording ? (slate ? "carta de ajuste" : "grabación+preview") : "preview", string.Join(' ', args));

        // StallTimeout 30 s (no 15): el watchdog solo debe matar un FFmpeg realmente COLGADO, no uno que se
        // atascó unos segundos por un pico de E/S de disco compartido entre los N canales (o CPU/GPU saturada).
        // Con 15 s, bajo carga de 4 canales un atasco transitorio del disco (FFmpeg bloquea al escribir → deja de
        // reportar progreso) provocaba un reinicio innecesario = un CORTE visible en esa grabación. Un cuelgue de
        // verdad es permanente, así que esperar 30 s para confirmarlo no cuesta nada y evita esos falsos cortes.
        // FinalizeOnStop solo en GRABACIÓN: el proceso de solo-preview no escribe archivo, así que al detenerlo
        // (p. ej. al reasignar la entrada) se mata de inmediato en vez de esperar la «q» —que se colgaría si la
        // fuente perdió señal y FFmpeg quedó atascado en la lectura del driver—. La grabación sí finaliza el MP4.
        _supervisor = new FfmpegProcessSupervisor(_locator.FfmpegPath, _log)
        {
            StallTimeout = TimeSpan.FromSeconds(30),
            FinalizeOnStop = recording,
            // La GRABACIÓN de la fuente en vivo NO se auto-relanza en el supervisor (reabriría el archivo con -y y
            // lo truncaría): su caída la gestiona el motor en una PIEZA NUEVA (OnRecordingProcessDied). El PREVIEW
            // (sin archivo) y la carta de ajuste (bars generadas; truncarlas es cosmético) sí se auto-relanzan. (N1.)
            RestartInternally = !recording || slate,
            // Entradas de red: el proceso lee del relé de un receptor permanente; cuando el emisor cierra, el relé cierra y
            // FFmpeg sale con 0 → en preview se relanza y espera al flujo siguiente; y mientras espera no produce progreso
            // (no está colgado). En grabación esa salida limpia la recupera el motor en una pieza nueva (Completed).
            RestartOnCleanExit = !recording && !slate && _source?.RestartsAfterEndOfStream == true,
            IgnoreStallUntilFirstProgress = !slate && _source?.WaitsForPeer == true,
            // #55: sonda para que el watchdog detecte un archivo que NO crece aunque FFmpeg reporte progreso
            // (encoder colgado / escritura muerta). Solo en grabación; negativo si está pausado (no evaluable).
            RecordedBytesProbe = recording ? () => _state == RecordingState.Paused ? -1L : CurrentSessionBytes() : (Func<long>?)null,
            // Sonda del DISCO de destino: cuando el archivo no crece, distingue «FFmpeg no escribe» (se mata y se
            // sigue en pieza nueva) de «el disco no acepta escrituras» (se espera con alarma; matar dejaría el
            // archivo sin índice y no arreglaría nada). Incidente 2026-09-06.
            VolumeProbe = recording ? () => Baioss.Record.Engine.FFmpeg.VolumeProbe.IsResponsiveAsync(dir, TimeSpan.FromSeconds(5)) : null,
        };
        Attach(_supervisor);
        await _supervisor.StartAsync(args, ct).ConfigureAwait(false);
        return sink;
    }

    /// <summary>Espera (con tope) a que el proceso nuevo tome el mando del preview —o a que uno más nuevo aún lo releve— y
    /// entonces retira el viejo y su sumidero.</summary>
    private async Task HandoffAsync(PreviewSink incoming, FfmpegProcessSupervisor outgoing, PreviewSink? outgoingSink)
    {
        try
        {
            using var timeout = new CancellationTokenSource(HandoffTimeout);
            await incoming.TookOver.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Canal {Key}: el proceso nuevo no tomó el preview en {Timeout:0} s; se retira el anterior igualmente.",
                _channelKey, HandoffTimeout.TotalSeconds);
        }
        await RetireAsync(outgoing, outgoingSink).ConfigureAwait(false);
    }

    private async Task RetireAsync(FfmpegProcessSupervisor supervisor, PreviewSink? sink)
    {
        try { await supervisor.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Canal {Key}: fallo al retirar el proceso anterior.", _channelKey); }
        if (sink is not null)
        {
            lock (_liveSinks) _liveSinks.Remove(sink);
            await sink.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>El sumidero <paramref name="generation"/> tomó el mando: los más viejos que aún esperaban su turno quedan
    /// relevados (sus relevos pendientes retiran su proceso sin esperar más).</summary>
    private void SupersedeOlderSinks(int generation)
    {
        lock (_liveSinks)
            foreach (var s in _liveSinks)
                if (s.Generation < generation) s.MarkSuperseded();
    }

    private void Attach(FfmpegProcessSupervisor supervisor)
    {
        supervisor.VolumeStalled += OnVolumeStalled;
        supervisor.ProgressLine += OnProgress;
        supervisor.LogLine += OnLog;
        supervisor.Crashed += OnRecordingProcessDied;
        supervisor.Completed += OnRecordingProcessCompleted; // salida LIMPIA inesperada durante grabación (N6)
        supervisor.Restarted += OnSupervisorRestarted;         // el preview se relanza: la fuente dejó de entregar
    }

    private void Detach(FfmpegProcessSupervisor supervisor)
    {
        supervisor.VolumeStalled -= OnVolumeStalled;
        supervisor.ProgressLine -= OnProgress;
        supervisor.LogLine -= OnLog;
        supervisor.Crashed -= OnRecordingProcessDied;
        supervisor.Completed -= OnRecordingProcessCompleted;
        supervisor.Restarted -= OnSupervisorRestarted;
    }

    private void OnVolumeStalled(object? sender, bool stalled) => RaiseAlarm(AlarmType.DiskStalled, stalled);

    /// <summary>
    /// Servidor TCP loopback al que un proceso FFmpeg envía los frames BGRA del preview. Uno por proceso: así el proceso
    /// nuevo y el viejo pueden pintar a la vez durante el relevo. <see cref="TookOver"/> se completa cuando este sumidero
    /// toma el mando (pinta su primer frame) o cuando uno más nuevo lo releva sin que llegara a pintar.
    /// </summary>
    private sealed class PreviewSink : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _tookOver = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _loop;

        public PreviewSink(int generation)
        {
            Generation = generation;
            Listener = new TcpListener(IPAddress.Loopback, 0);
            Listener.Start();
            Port = ((IPEndPoint)Listener.LocalEndpoint).Port;
        }

        public int Generation { get; }
        public int Port { get; }
        public TcpListener Listener { get; }
        /// <summary>Se completa cuando este sumidero toma el mando (pinta su primer frame) o uno más nuevo lo releva.</summary>
        public Task TookOver => _tookOver.Task;

        /// <summary>Con relevo: esperar a que el proceso lea en directo y sus frames lleguen a cadencia real antes de tomar el
        /// mando (ver <see cref="ReadyToTakeOver"/>). Sin relevo, el primer frame manda.</summary>
        public bool SettleBeforeTakeover { get; init; }
        /// <summary>Intervalo nominal entre frames de la fuente (ms): la cadencia «real» que se espera.</summary>
        public double NominalIntervalMs { get; init; } = 40;
        /// <summary>¿Lee ya el proceso de este sumidero la entrada en directo (sin pre-roll ni atraso pendientes)? La fuente lo
        /// sabe (<see cref="ICaptureSource.NewestConsumerIsLive"/>); null = no se puede saber (se juzga solo por cadencia).</summary>
        public Func<bool>? IsLive { get; init; }
        /// <summary>Cuánto debe SOSTENERSE el estado «en directo» antes de tomar el mando: un instante al día entre dos fragmentos
        /// no vale, y en ese tiempo el proceso vacía también los búferes del socket que la fuente no ve.</summary>
        public TimeSpan LiveConfirmation { get; init; } = TimeSpan.FromSeconds(1);
        /// <summary>Tope: pasado este tiempo desde el primer frame se toma el mando aunque no se haya asentado (fuente a ráfagas,
        /// tasa nominal desconocida). Generoso: el proceso viejo sigue pintando el directo mientras tanto.</summary>
        public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(10);

        private readonly Queue<long> _arrivals = new();
        private long _firstArrival;
        private long _liveSince;
        private static readonly TimeSpan RateWindow = TimeSpan.FromMilliseconds(500);

        /// <summary>¿Puede este sumidero tomar el mando con el frame que acaba de llegar? Sin asentamiento, sí. Con él, cuando
        /// (1) la fuente lleva <see cref="LiveConfirmation"/> confirmando que el proceso lee en directo (agotó el pre-roll y el
        /// atraso acumulado mientras lo digería) y (2) en el último medio segundo han llegado como mucho un 25 % más frames de
        /// los que caben a la cadencia nominal (mientras vacía los últimos búferes llegan al doble o más); o al vencer
        /// <see cref="SettleTimeout"/>. Esperar de más no cuesta nada: el proceso viejo sigue pintando el directo mientras tanto.</summary>
        public bool ReadyToTakeOver(long nowTicks)
        {
            if (!SettleBeforeTakeover) return true;
            if (_firstArrival == 0) _firstArrival = nowTicks;
            _arrivals.Enqueue(nowTicks);
            while (_arrivals.Count > 0 && Stopwatch.GetElapsedTime(_arrivals.Peek(), nowTicks) > RateWindow) _arrivals.Dequeue();
            if (Stopwatch.GetElapsedTime(_firstArrival, nowTicks) >= SettleTimeout) return true;
            // (1) En directo de forma sostenida: cualquier instante atrasado reinicia la cuenta.
            if (IsLive is not null && !IsLive()) { _liveSince = 0; return false; }
            if (_liveSince == 0) _liveSince = nowTicks;
            if (Stopwatch.GetElapsedTime(_liveSince, nowTicks) < LiveConfirmation) return false;
            // (2) A cadencia real.
            if (Stopwatch.GetElapsedTime(_firstArrival, nowTicks) < RateWindow) return false;
            double expected = RateWindow.TotalMilliseconds / NominalIntervalMs;
            return _arrivals.Count <= expected * 1.25 + 1;
        }

        public void Run(Func<PreviewSink, CancellationToken, Task> loop) => _loop = Task.Run(() => loop(this, _cts.Token), _cts.Token);
        public void MarkTookOver() => _tookOver.TrySetResult();
        /// <summary>Uno más nuevo tomó el mando antes que este: su relevo pendiente ya no debe esperar nada.</summary>
        public void MarkSuperseded() => _tookOver.TrySetResult();

        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _cts.CancelAsync().ConfigureAwait(false);
            try { Listener.Stop(); } catch { /* noop */ }
            if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { /* cancelación esperada */ } }
            _tookOver.TrySetCanceled();
            _cts.Dispose();
        }
    }

    // El proceso FFmpeg se conecta como cliente al servidor TCP de su sumidero; aquí leemos los frames BGRA y, al
    // relanzarse el proceso (respawn del supervisor), re-aceptamos la nueva conexión.
    private async Task AcceptLoopAsync(PreviewSink sink, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await sink.Listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                await using var stream = client.GetStream();
                await ReadFramesAsync(sink, stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Preview TCP: reintentando aceptación."); }
        }
    }

    private async Task ReadFramesAsync(PreviewSink sink, NetworkStream stream, CancellationToken ct)
    {
        int frameSize = FrameWidth * 4 * FrameHeight;
        // Los búferes salen del pool del colchón (PreviewPacer), que los REUTILIZA sin copiar (clonar ~0,9 MB por frame a
        // ~30 fps × N canales presionaba al GC, sobre todo al Large Object Heap) y nunca vuelve a prestar uno que la UI
        // pueda estar leyendo aún (cuarentena de los 2 últimos entregados: el anillo de 3 que había por lector).
        // Sin colchón, el frame se entrega aquí mismo, en este hilo, como siempre; con colchón se encola y lo entrega el
        // hilo del colchón a la cadencia de la fuente.
        var pacer = Pacer;
        while (!ct.IsCancellationRequested)
        {
            var buffer = pacer.Rent();
            bool handedOver = false;
            try
            {
                int read = 0;
                while (read < frameSize)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(read, frameSize - read), ct).ConfigureAwait(false);
                    if (n == 0) return; // el proceso cerró la conexión (reinicio) → volver a aceptar
                    read += n;
                }
                // Relevo: el sumidero más nuevo toma el mando en cuanto su proceso lee en directo y a cadencia real (o con su
                // primer frame si no hay relevo); hasta entonces sus frames se descartan (se leen igual: FFmpeg no debe
                // bloquearse), y los de un sumidero ya relevado también.
                if (sink.Generation != _activeSink)
                {
                    if (sink.Generation < _activeSink || !sink.ReadyToTakeOver(Stopwatch.GetTimestamp())) continue;
                    _activeSink = sink.Generation;
                    SupersedeOlderSinks(sink.Generation);
                }
                sink.MarkTookOver();
                handedOver = true;
                pacer.Enqueue(buffer);
            }
            finally { if (!handedOver) pacer.Discard(buffer); }
        }
    }

    private void OnProgress(object? sender, string line)
    {
        var stats = _parser.Feed(line);
        if (stats is null) return;
        // El bitrate del parser es el agregado de FFmpeg, dominado por el preview CRUDO (rawvideo a un socket,
        // ~180 Mbps), no el del archivo. Al grabar se sustituye por el bitrate REAL medido por crecimiento del
        // archivo en disco, para que la UI muestre el bitrate real de la grabación. (Fix del bitrate del preview.)
        if (_recordDir is not null && _state is RecordingState.Recording or RecordingState.Starting or RecordingState.Paused)
        {
            SampleRealBitrate();
            // RecordedBytes: bytes de la sesión, para que el monitor de salud calcule el ritmo de escritura sin
            // recorrer la carpeta del canal (que crece sin límite en 24/7). (Auditoría N16.)
            stats = stats with { Bitrate = new Bitrate(_realBitrateBps), RecordedBytes = _recordedBytes };
        }
        Stats = stats;
        StatsUpdated?.Invoke(this, stats);
    }

    /// <summary>Estima el bitrate REAL de la grabación por el crecimiento de los archivos de ESTA sesión en
    /// disco (no el del parser, contaminado por el preview crudo). Re-mide como mucho cada ~2 s y suaviza con
    /// EMA para que el display no salte con los flush de fragmentos del fMP4. Guarda además los bytes de la
    /// sesión en <see cref="_recordedBytes"/> para exponerlos en RecorderStats. (Auditoría N16.)</summary>
    private void SampleRealBitrate()
    {
        var now = DateTimeOffset.UtcNow;
        double secs = (now - _lastRecSampleUtc).TotalSeconds;
        if (secs < 1.5) return; // no re-medir en cada línea de progreso (~1/s); ~cada 2 s basta
        long bytes = CurrentSessionBytes();
        _recordedBytes = bytes;
        if (_lastRecBytes > 0 && bytes >= _lastRecBytes) // 1ª muestra: solo fija la base (sesión aún vacía)
        {
            long bps = (long)((bytes - _lastRecBytes) * 8 / secs);
            _realBitrateBps = _realBitrateBps > 0 ? (long)(0.6 * _realBitrateBps + 0.4 * bps) : bps;
        }
        _lastRecBytes = bytes;
        _lastRecSampleUtc = now;
    }

    /// <summary>
    /// Bytes en disco de la sesión de grabación EN CURSO, medidos SIN recorrer toda la carpeta del canal (que
    /// acumula meses de grabaciones en 24/7 → un escaneo O(nº archivos) en el hot-path del progreso). En archivo
    /// único basta una <c>stat</c> del propio archivo; segmentada, la suma de los segmentos de ESTA sesión
    /// (glob por nombre base), no de todos los archivos de la carpeta. Best-effort. (Auditoría N16.)
    /// </summary>
    private long CurrentSessionBytes()
    {
        try
        {
            if (_recordFile is not null) // archivo único
                return new FileInfo(_recordFile) is { Exists: true } fi ? fi.Length : 0;
            if (!string.IsNullOrEmpty(_segDir) && !string.IsNullOrEmpty(_segGlob) && Directory.Exists(_segDir))
            {
                long sum = 0;
                foreach (var f in Directory.EnumerateFiles(_segDir, _segGlob)) // glob = solo los segmentos de esta sesión
                    try { sum += new FileInfo(f).Length; } catch { /* archivo en escritura/borrado */ }
                return sum;
            }
        }
        catch { /* carpeta inaccesible momentáneamente */ }
        return 0;
    }

    /// <summary>
    /// El supervisor relanzó el proceso de PREVIEW tras una salida inesperada (o un fin de flujo de una fuente de red):
    /// hasta que el nuevo proceso abra la entrada no hay captura. En grabación no se toca nada: esa caída ya la cuenta
    /// OnRecordingProcessDied (auditoría RecordingInterrupted + pieza nueva o carta de ajuste).
    /// </summary>
    private void OnSupervisorRestarted(object? sender, int restartCount)
    {
        if (_state == RecordingState.Idle && !_slate) _source?.ReportDeviceOpen(false);
    }

    private void OnLog(object? sender, string line)
    {
        // DeckLink en autodetección: FFmpeg cuenta al abrir qué modo detectó la tarjeta («Found Decklink mode 1920 x 1080
        // with rate 29.97(i)») o que no detectó nada. Se lo pasa a la fuente, que lo publica en su señal: el panel enseña
        // el formato real (antes decía «—» con la tarjeta capturando) y pasa a SIN SEÑAL cuando no hay nada que
        // detectar, en vez de fingir «SEÑAL OK» con el preview en negro mientras el supervisor relanza el proceso.
        if (_source is { } src)
        {
            if (DecklinkModeParser.TryParseFoundMode(line) is { } mode) { src.ReportDetectedMode(mode); return; }
            if (DecklinkModeParser.IsAutodetectFailure(line))
            {
                if (!ReferenceEquals(_autodetectLoggedFor, src))
                {
                    _autodetectLoggedFor = src;
                    _log.LogWarning("Canal {Key}: la tarjeta no autodetectó la señal ({Line}). Sin señal o cable, tarjeta en uso, o tarjeta/conector sin autodetección: fija el modo en Entradas → Modo/formato (o pulsa «Detectar señal»).", _channelKey, line.Trim());
                }
                src.ReportDetectedMode(null);
                return;
            }
            // Con modo fijo no hay autodetección: «Cannot enable video input» = la tarjeta la tiene otro proceso (otro
            // programa, u otro canal con la misma entrada), e «Input #0, decklink, from…» = el dispositivo abrió y captura.
            if (DecklinkModeParser.IsDeviceBusy(line))
            {
                if (!ReferenceEquals(_deviceBusyLoggedFor, src))
                {
                    _deviceBusyLoggedFor = src;
                    _log.LogWarning("Canal {Key}: la tarjeta está en uso por otro proceso ({Line}): otro programa (Media Express, OBS…) u otro canal con la misma entrada. Se reintenta hasta que quede libre.", _channelKey, line.Trim());
                }
                src.ReportDeviceOpen(false);
                return;
            }
            if (DecklinkModeParser.IsInputOpened(line)) { src.ReportDeviceOpen(true); return; }
        }

        // La tarjeta no admite los canales de audio pedidos (DeckLink): baja un escalón y reconstruye. Solo tiene
        // sentido con más de 2 canales pedidos; con 2, ese mensaje sería otro fallo (entrada de audio en uso, etc.).
        if (!_audioFallbackPending && _source is { AudioChannelCount: > 2 } &&
            line.Contains("Cannot enable audio input", StringComparison.Ordinal))
        {
            _audioFallbackPending = true;
            // Con un recuento fijo que la tarjeta no admite, el supervisor relanza y la línea vuelve en cada intento:
            // se registra solo la primera vez por fuente (TryReduceAudioChannelsAsync deja constancia del error).
            if (!ReferenceEquals(_audioRejectLoggedFor, _source))
                _log.LogWarning("Canal {Key}: el dispositivo rechazó {N} canales de audio → {Line}", _channelKey, _source.AudioChannelCount, line);
            _ = Task.Run(TryReduceAudioChannelsAsync);
            return;
        }

        // Fallo de APERTURA del codificador por hardware (NVENC agotado, driver/GPU ausente): degrada al
        // siguiente de la cadena (QSV→AMF→CPU) y reinicia, en lugar de dejar que el supervisor relance el
        // mismo argv en vano. Solo durante la grabación (el preview no lleva codificador de salida).
        var rec = _recordProfile;
        if (!_encoderOpenError && rec is not null &&
            _state is RecordingState.Recording or RecordingState.Starting &&
            FfmpegEncoderError.IsOpenFailure(line))
        {
            _encoderOpenError = true;
            _log.LogWarning("Canal {Key}: el codificador de vídeo '{Encoder}' no pudo abrir → {Line}",
                _channelKey, FfmpegCodecMap.VideoEncoder(rec.VideoCodec), line);
            _ = Task.Run(TryFallbackEncoderAsync);
            return;
        }

        // Alarmas de análisis: negro/congelado/silencio. Cada marca de FFmpeg → transición de alarma.
        if (FfmpegDetectParser.Parse(line) is { } d) { RaiseAlarm(d.Type, d.Active); return; }

        // Niveles de audio del filtro ebur128 ("… FTPK: -16.6 -16.9 dBFS …"): un true-peak por canal capturado
        // (2 estéreo; 8/16 con audio embebido multicanal). Los consumidores reparten por pares.
        if (FfmpegMeterParser.ParseTruePeaks(line) is { } peaks) AudioPeaksUpdated?.Invoke(this, peaks);
    }

    // --- Segmentación: cada archivo de segmento completo se emite como un Segment ---

    private async Task SegmentScanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { ScanSegments(includeNewest: false); }
            catch (Exception ex) { _log.LogDebug(ex, "Escaneo de segmentos: fallo."); }
        }
    }

    /// <summary>
    /// Emite los segmentos del directorio aún no notificados. Salvo en el escaneo final
    /// (<paramref name="includeNewest"/>), deja el archivo más reciente sin emitir porque suele ser el
    /// que FFmpeg está escribiendo; el siguiente segmento (o el stop) lo cerrará.
    /// </summary>
    private void ScanSegments(bool includeNewest)
    {
        if (string.IsNullOrEmpty(_segDir) || !Directory.Exists(_segDir)) return;
        var files = Directory.GetFiles(_segDir, _segGlob);
        if (files.Length == 0) return;
        // Orden cronológico por (prefijo, índice NUMÉRICO): así «_10» va tras «_9» (no como el orden textual,
        // que pondría «_10» antes de «_2») y la numeración 1-based sin relleno conserva la continuidad.
        Array.Sort(files, CompareSegment);
        int upTo = includeNewest ? files.Length : files.Length - 1;
        for (int i = 0; i < upTo; i++) EmitSegmentFile(files[i]);
    }

    private void EmitSegmentFile(string path, bool optimizeSeek = false)
    {
        if (!_emitted.Add(path)) return; // ya emitido
        _sessionFiles.Add(path);         // candidato a renombrar al detener una grabación manual
        var fi = new FileInfo(path);
        SegmentClosed?.Invoke(this, new Segment
        {
            SessionId = _sessionId,
            Index = _segmentIndex++,
            FilePath = path,
            Status = SegmentStatus.Completed,
            StartedAt = fi.Exists ? new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero) : _recordStart,
            EndedAt = fi.Exists ? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero) : DateTimeOffset.UtcNow,
            SizeBytes = fi.Exists ? fi.Length : 0,
        });
        var verify = VerifyRecordingAsync(path, optimizeSeek); // red de seguridad + (archivo único) optimización de seek
        // Se registra SIEMPRE para que el renombrado espere a que TERMINE (anti-carrera). Con remux, porque reescribe el
        // archivo in situ. Y sin él también: la verificación abre el archivo por su nombre a los 300 ms, y si para
        // entonces ya se renombró no lo encuentra → FALSA alarma «grabación sin verificar», RecordingFileUnverified de
        // 0 bytes en la auditoría y el segmento marcado Corrupt. Desde el diálogo de la aplicación era improbable (el
        // operador tarda en escribir); con el nombre pedido por la API al detener, el renombrado es inmediato y la
        // carrera era sistemática (medido). Poda los ya completados para no crecer sin límite en una grabación larga
        // con muchas piezas. (Auditoría N29.)
        lock (_optimizeLock) { _pendingOptimizes.RemoveAll(t => t.IsCompleted); _pendingOptimizes.Add(verify); }
    }

    /// <summary>
    /// Verifica con ffprobe que un archivo recién cerrado es REPRODUCIBLE (tiene pistas y duración). Si no
    /// —p. ej. un MP4 sin <c>moov</c> por un corte abrupto, o 0 bytes—, enciende la alarma
    /// RecordingUnverified para que el operador lo sepa al momento, en vez de descubrirlo días después.
    /// Best-effort y en segundo plano: nunca interrumpe la grabación en curso.
    /// </summary>
    private async Task VerifyRecordingAsync(string path, bool optimizeSeek = false)
    {
        try
        {
            await Task.Delay(300).ConfigureAwait(false); // el handle puede tardar un instante en liberarse
            var probe = await _locator.ProbeMediaAsync(path).ConfigureAwait(false);
            if (probe.IsPlayable)
            {
                _log.LogDebug("Canal {Key}: {File} verificado ({Codec}, {Dur:0.0}s).",
                    _channelKey, Path.GetFileName(path), probe.VideoCodec ?? "audio", probe.DurationSeconds);

                // Optimización de búsqueda (solo archivo único): el fMP4 fragmentado es robusto ante cortes pero
                // su seek en VLC es por estimación —macrobloques hasta el keyframe, peor en archivos grandes—.
                // Una vez VERIFICADO que es reproducible, se reescribe a MP4 estándar con el índice al inicio
                // (faststart), SIN recodificar. Si el remux falla, el original fMP4 (ya válido) se conserva.
                if (optimizeSeek)
                {
                    long bytes = 0;
                    try { bytes = new FileInfo(path).Length; } catch { /* si no se puede medir, se intenta igual */ }
                    long cap = _locator.FaststartMaxBytes;
                    if (cap > 0 && bytes > cap)
                    {
                        // Archivo GRANDE: el remux faststart reescribiría el archivo entero (lectura + escritura
                        // completas), saturando el disco y compitiendo con las grabaciones activas (que podrían
                        // perder frames). Se conserva el fMP4 (válido y reproducible, solo con seek por estimación).
                        // Para archivos largos con búsqueda óptima SIN reescribir: contenedor MKV o segmentación.
                        _log.LogInformation("Canal {Key}: {File} ({GB:0.0} GB) supera el límite de optimización " +
                            "faststart ({Cap:0.0} GB); se conserva el fMP4 sin reescribir (evita saturar el disco). " +
                            "Para búsqueda óptima en archivos grandes, usa un preset MKV o segmentación.",
                            _channelKey, Path.GetFileName(path), bytes / 1_000_000_000.0, cap / 1_000_000_000.0);
                    }
                    else
                    {
                        try
                        {
                            if (await _locator.RemuxFaststartAsync(path).ConfigureAwait(false))
                                _log.LogInformation("Canal {Key}: {File} optimizado para búsqueda (índice al inicio).",
                                    _channelKey, Path.GetFileName(path));
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Canal {Key}: no se pudo optimizar la búsqueda de {File} (se conserva el original).",
                                _channelKey, Path.GetFileName(path));
                        }
                    }
                }
            }
            else
            {
                _log.LogError("Canal {Key}: {File} NO pasó la verificación (sin pistas/duración válidas): posible grabación dañada.",
                    _channelKey, path);
                RaiseAlarm(AlarmType.RecordingUnverified, true);
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { /* solo informativo */ }
                FileUnverified?.Invoke(this, (path, size));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Canal {Key}: no se pudo verificar {File}.", _channelKey, path); }
    }

    private async Task StopSegmentScanAsync()
    {
        if (_segScanCts is null) return;
        await _segScanCts.CancelAsync().ConfigureAwait(false);
        if (_segScanLoop is not null) { try { await _segScanLoop.ConfigureAwait(false); } catch { /* cancelación */ } }
        _segScanCts.Dispose();
        _segScanCts = null; _segScanLoop = null;
    }

    // --- Carta de ajuste (slate) ante pérdida de señal en vivo ---

    /// <summary>
    /// El proceso de GRABACIÓN murió inesperadamente (crash / kill del watchdog / la entrada falló) y el
    /// supervisor NO lo relanzó —hacerlo reabriría el archivo con <c>-y</c> y lo truncaría—. El motor lo
    /// reconstruye en una PIEZA NUEVA: si el perfil pide carta de ajuste, entra en barras; si no, reintenta la
    /// fuente. La pieza anterior se conserva (ReplaceProcessAsync la emite como segmento). Con backoff acotado
    /// para no crear piezas en bucle si la fuente falla en serie. Se despacha a otra tarea: no se puede disponer
    /// el supervisor desde su propio hilo de evento. (Auditoría N1.)
    /// </summary>
    /// <summary>
    /// FFmpeg salió LIMPIO (código 0) mientras seguíamos queriendo grabar: el emisor NDI cerró su TCP, una
    /// fuente finita llegó a EOF, o el watchdog lo finalizó con «q». NO fue un stop nuestro (ese desuscribe este
    /// handler antes de disponer) → recupera en una pieza nueva, igual que una caída, en vez de quedar «grabando»
    /// sin proceso y sin alarma. La guarda de estado de OnRecordingProcessDied es el respaldo. (Auditoría N6.)
    /// </summary>
    private void OnRecordingProcessCompleted(object? sender, int exitCode)
    {
        _log.LogWarning("Canal {Key}: el proceso de grabación finalizó por su cuenta (EOF/cierre de la fuente); recuperando.", _channelKey);
        OnRecordingProcessDied(sender, exitCode);
    }

    private void OnRecordingProcessDied(object? sender, int exitCode)
    {
        if (_disposed || _slate || _slatePending || _recovering) return;
        // Un fallo de APERTURA de codificador lo resuelve el fallback de codificador (degradar el códec), que ya
        // reinicia el proceso; no lo tratamos aquí como caída de la fuente. Ídem el retroceso de canales de audio.
        if (_encoderOpenError || _fallbackPending || _audioFallbackPending) return;
        if (_state is not (RecordingState.Recording or RecordingState.Starting)) return;

        _recovering = true;
        _log.LogWarning("Canal {Key}: el proceso de grabación murió (código {Code}); recuperando en una PIEZA NUEVA (sin truncar la anterior).", _channelKey, exitCode);
        // A la auditoría: es el suceso que explica un archivo cortado y un hueco de segundos. −1 es el código con
        // el que sale un proceso MATADO (watchdog por estancamiento, o alguien desde fuera); cualquier otro es
        // FFmpeg saliendo por su cuenta (la entrada falló, el codificador, un error de escritura…).
        RecordingInterrupted?.Invoke(this, (exitCode, exitCode == -1
            ? "proceso terminado a la fuerza (watchdog por estancamiento, o desde fuera)"
            : $"FFmpeg salió con código {exitCode}"));
        _ = Task.Run(() => RecoverRecordingAsync(exitCode));
    }

    /// <summary>
    /// Reconstruye la grabación tras la caída del proceso: espera un backoff, y bajo el semáforo reconstruye vía
    /// <see cref="ReplaceProcessAsync"/> —que emite la pieza anterior y arranca una nueva con nombre único / nº de
    /// segmento recalculado, sin reabrir el mismo archivo—. Si la reconstrucción falla (la fuente sigue caída),
    /// reintenta con más backoff hasta que la señal vuelva. <see cref="_recovering"/> se mantiene mientras dure
    /// la cadena de reintentos y se limpia al lograrlo o al dejar de grabar. (Auditoría N1.)
    /// </summary>
    private async Task RecoverRecordingAsync(int exitCode)
    {
        bool retry = false;
        try
        {
            try { await Task.Delay(NextRecordRestartDelay()).ConfigureAwait(false); } catch { /* noop */ }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _state is not (RecordingState.Recording or RecordingState.Starting)) return;
                bool slate = _recordProfile?.SlateOnSignalLoss == true;
                if (slate) { _slate = true; _slateSince = DateTimeOffset.UtcNow; _slateAlarmRaised = false; }
                await ReplaceProcessAsync(recording: true, slate: slate, CancellationToken.None).ConfigureAwait(false);
                if (slate) { RaiseAlarm(AlarmType.Slate, true); StartRecoveryProbe(); }
            }
            catch (Exception ex)
            {
                // Una fuente que se auto-reporta y sigue SIN SEÑAL (el emisor de red no ha vuelto) no puede reconstruirse
                // todavía: no es un error, es la espera normal; se reintenta con backoff hasta que la señal vuelva.
                if (_source is { SelfReportsRecovery: true, CurrentSignal.State: SignalState.NoSignal })
                    _log.LogWarning("Canal {Key}: la fuente sigue sin señal; la grabación continuará en una pieza nueva cuando vuelva.", _channelKey);
                else
                    _log.LogError(ex, "Canal {Key}: fallo al recuperar la grabación tras la caída; se reintentará con backoff.", _channelKey);
                retry = !_disposed && _state is (RecordingState.Recording or RecordingState.Starting);
            }
            finally { _gate.Release(); }
        }
        finally
        {
            if (retry) _ = Task.Run(() => RecoverRecordingAsync(exitCode)); // mantiene _recovering=true durante la cadena
            else _recovering = false;
        }
    }

    /// <summary>Backoff exponencial acotado (1→2→4→8→16→30 s) entre reintentos de recuperación de la grabación.
    /// Se resetea si el proceso anterior vivió sano &gt;60 s (una caída aislada no arrastra el retardo de una racha
    /// vieja; aborda también N24). El mínimo de 1 s garantiza además que el nombre por defecto de la pieza nueva
    /// (precisión de SEGUNDO) difiera del de la anterior, evitando cualquier colisión con <c>-y</c>.</summary>
    private TimeSpan NextRecordRestartDelay()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRecordDeathUtc > TimeSpan.FromSeconds(60)) _recordRestartCount = 0;
        _lastRecordDeathUtc = now;
        int n = ++_recordRestartCount;
        double ms = Math.Min(30_000, 1000 * Math.Pow(2, Math.Min(n - 1, 5)));
        return TimeSpan.FromMilliseconds(ms);
    }

    private async Task EnterSlateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_slate || _state is not (RecordingState.Recording or RecordingState.Starting)) return;
            _slate = true;
            _slateSince = DateTimeOffset.UtcNow;
            _slateAlarmRaised = false;
            await ReplaceProcessAsync(recording: true, slate: true, CancellationToken.None).ConfigureAwait(false);
            RaiseAlarm(AlarmType.Slate, true);
            StartRecoveryProbe();
        }
        catch (Exception ex)
        {
            // El pipeline de barras no arrancó, pero mantén viva la sonda de recuperación: reconstruirá la
            // grabación cuando la señal vuelva. Sin esto el canal quedaría «grabando» sin proceso NI recuperación. (N3.)
            _log.LogError(ex, "Canal {Key}: error al entrar en carta de ajuste; se reintentará la recuperación.", _channelKey);
            StartRecoveryProbe();
        }
        finally { _slatePending = false; _gate.Release(); }
    }

    /// <summary>Sale de la carta de ajuste reconstruyendo la fuente en vivo. Devuelve <c>true</c> si lo logró;
    /// <c>false</c> si el rebuild falló (la señal volvió a caer) → sigue en slate y el bucle de recuperación
    /// reintentará. <c>_slate</c> solo se baja TRAS un rebuild exitoso, para no quedar «grabando» sin proceso. (N3.)</summary>
    private async Task<bool> ExitSlateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_slate || _state is not (RecordingState.Recording or RecordingState.Starting)) return true; // ya no aplica
            await ReplaceProcessAsync(recording: true, slate: false, CancellationToken.None).ConfigureAwait(false);
            _slate = false; // solo tras reconstruir con ÉXITO: si ReplaceProcessAsync lanzó, seguimos en slate
            RaiseAlarm(AlarmType.Slate, false);
            RaiseAlarm(AlarmType.SignalLoss, false); // la señal volvió: retira la alarma de slate prolongado
            _slateAlarmRaised = false;
            _log.LogInformation("Canal {Key}: señal recuperada; reanudando la fuente.", _channelKey);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Canal {Key}: error al salir de carta de ajuste; se mantiene el slate y se reintentará.", _channelKey);
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// La fuente reporta cambio de señal (en NDI ahora también la PÉRDIDA en caliente, vía el receptor):
    /// entra en carta de ajuste de inmediato al perderla —sin esperar al watchdog de 15 s— y la abandona al
    /// recuperarla. Las guardas de EnterSlate/ExitSlate hacen la operación idempotente frente al watchdog y
    /// al sondeo de recuperación, así que no hay doble slate. (Auditoría 24/7, C3.)
    /// </summary>
    private void OnSourceSignalChanged(object? sender, SignalInfo info)
    {
        if (_disposed) return;
        if (info.State == SignalState.Locked)
        {
            if (_slate) _ = ExitSlateAsync(); // la señal volvió: reanuda la fuente real
            return;
        }
        // Pérdida/inestabilidad: misma política y guardas que OnSupervisorRestarted, pero proactiva.
        if (_slate || _slatePending) return;
        if (_encoderOpenError || _fallbackPending) return;
        if (_state is not (RecordingState.Recording or RecordingState.Starting)) return;
        if (_recordProfile?.SlateOnSignalLoss != true) return;

        _slatePending = true;
        _log.LogWarning("Canal {Key}: la fuente reportó pérdida de señal; pasando a carta de ajuste.", _channelKey);
        _ = Task.Run(EnterSlateAsync);
    }

    // --- Fallback de codificador ante fallo de apertura por hardware ---

    /// <summary>
    /// El codificador de vídeo de grabación no pudo abrir (lo detectó <see cref="FfmpegEncoderError"/> en el
    /// stderr). Degrada al siguiente de la <see cref="EncoderFallbackChain"/> (QSV→AMF→CPU) sobre una COPIA
    /// del perfil —no muta el del canal— y reinicia el proceso. Es iterativa: si el alternativo tampoco
    /// abre, su stderr vuelve a disparar este método y baja otro escalón hasta CPU (libx264), que siempre
    /// abre. Si ya no hay alternativa, lo registra y deja seguir el flujo normal.
    /// </summary>
    private async Task TryFallbackEncoderAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_fallbackPending) return;
            var current = _recordProfile;
            if (current is null || _state is not (RecordingState.Recording or RecordingState.Starting)) return;

            var next = EncoderFallbackChain.Next(current.VideoCodec);
            if (next is null)
            {
                _log.LogError("Canal {Key}: el codificador '{Encoder}' no abre y no hay alternativa (ya es CPU).",
                    _channelKey, FfmpegCodecMap.VideoEncoder(current.VideoCodec));
                return;
            }

            _fallbackPending = true;
            var from = current.VideoCodec;

            // Copia del perfil con el códec degradado y decode por software (HwAccel.None): universal, evita
            // dejar frames en una GPU de distinta familia que el nuevo codificador no podría tomar. NO se
            // muta el perfil persistido del canal: la próxima grabación vuelve a intentar el códec elegido.
            var degraded = current.Clone();
            degraded.VideoCodec = next.Value;
            degraded.HwAccel = HwAccel.None;
            _recordProfile = degraded;

            _log.LogWarning("Canal {Key}: codificador '{From}' no disponible → degradando a '{To}' y reiniciando la grabación.",
                _channelKey, FfmpegCodecMap.VideoEncoder(from), FfmpegCodecMap.VideoEncoder(next.Value));

            // ReplaceProcessAsync resetea _encoderOpenError: el proceso entrante (con el códec degradado) se
            // re-evalúa por su propio stderr; si tampoco abre, disparará otro escalón.
            await ReplaceProcessAsync(recording: true, slate: _slate, CancellationToken.None).ConfigureAwait(false);
            RaiseAlarm(AlarmType.EncoderFallback, true);
        }
        catch (Exception ex) { _log.LogError(ex, "Canal {Key}: error al degradar el codificador.", _channelKey); }
        finally { _fallbackPending = false; _gate.Release(); }
    }

    /// <summary>
    /// La tarjeta rechazó los canales de audio pedidos (DeckLink: «Cannot enable audio input» al pedir 16 u 8 en un
    /// modelo que no los admite). Con «audio_channels=auto» la fuente baja un escalón (16→8→2) y se reconstruye el
    /// proceso con el argv nuevo, en el mismo modo (preview o grabación). Con un recuento fijo solo se registra: la
    /// decisión es del operador (elige en el gestor de entradas un valor que la tarjeta admita).
    /// </summary>
    private async Task TryReduceAudioChannelsAsync()
    {
        bool resumeRecovery = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var src = _source;
            if (_disposed || src is null) return;
            int before = src.AudioChannelCount;
            if (!src.TryReduceAudioChannels())
            {
                // Una vez por fuente: el supervisor relanza el preview y el mensaje se repetiría en cada intento.
                if (!ReferenceEquals(_audioRejectLoggedFor, src))
                {
                    _audioRejectLoggedFor = src;
                    _log.LogError("Canal {Key}: el dispositivo no admite {N} canales de audio y la entrada no permite bajar; elige en «Entradas» un valor que la tarjeta admita (2 u 8).", _channelKey, before);
                }
                // No hay escalón al que bajar, así que este método NO reinicia nada. Mientras _audioFallbackPending
                // estaba activo, OnRecordingProcessDied se abstuvo de recuperar: si el proceso murió en esa ventana
                // (este método puede esperar el semáforo tras StartRecording) nadie lo levantaría y el canal quedaría
                // «grabando» sin proceso. Se devuelve la caída a su camino normal (pieza nueva / carta de ajuste).
                resumeRecovery = _state is RecordingState.Recording or RecordingState.Starting;
                return;
            }
            _log.LogWarning("Canal {Key}: el dispositivo no admite {From} canales de audio; se reintenta con {To}.", _channelKey, before, src.AudioChannelCount);
            bool recording = _state is RecordingState.Recording or RecordingState.Starting;
            await ReplaceProcessAsync(recording, recording && _slate, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogError(ex, "Canal {Key}: error al bajar los canales de audio.", _channelKey); }
        finally
        {
            _audioFallbackPending = false;
            _gate.Release();
            // Fuera del semáforo y con la bandera ya bajada; _recovering evita duplicar si la caída real llega después.
            if (resumeRecovery) OnRecordingProcessDied(this, 1);
        }
    }

    private void StartRecoveryProbe()
    {
        _recoveryCts?.Dispose(); // dispone el del ciclo de slate anterior (ya finalizado): sin fuga de CTS. (#53)
        _recoveryCts = new CancellationTokenSource();
        _recoveryLoop = Task.Run(() => RecoveryLoopAsync(_recoveryCts.Token));
    }

    private void StopRecoveryProbe()
    {
        try { _recoveryCts?.Cancel(); } catch { /* dispuesto */ }
    }

    private async Task RecoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // N30: si la grabación ya se detuvo (Stop mientras entrábamos en slate, antes de que StopRecoveryProbe
            // cancelara este bucle recién arrancado), NO sigas sondeando el dispositivo cada 5 s en Idle (cada
            // sondeo spawnea un ffmpeg) ni escales a una alarma de señal espuria: sal del bucle.
            if (!_slate || _state is not (RecordingState.Recording or RecordingState.Starting)) return;

            // Slate PROLONGADO: si la señal lleva sin volver más de SlateAlarmAfter, escala a alarma crítica
            // (SignalLoss) para que el operador actúe. La grabación de barras sigue (por si la señal vuelve).
            if (!_slateAlarmRaised && DateTimeOffset.UtcNow - _slateSince >= SlateAlarmAfter)
            {
                _slateAlarmRaised = true;
                _log.LogError("Canal {Key}: la señal lleva sin recuperarse {Min:0} min; carta de ajuste prolongada.", _channelKey, SlateAlarmAfter.TotalMinutes);
                RaiseAlarm(AlarmType.SignalLoss, true);
            }

            // Recuperación por SONDEO del dispositivo — SOLO para fuentes que NO se auto-reportan. Una fuente NDI
            // señala la vuelta de la señal ella misma (el receptor detecta presencia → SignalChanged → ExitSlate en
            // OnSourceSignalChanged), así que sondearla es redundante y DAÑINO: el sondeo abre un ffmpeg que se
            // conecta a los sockets del PROPIO receptor (no prueba la fuente NDI real), compite con el pipeline y da
            // falsos positivos. Para NDI seguimos vigilando el slate PROLONGADO (arriba) pero no sondeamos. (#39/#59.)
            if (_source?.SelfReportsRecovery == true) continue;

            if (await ProbeDeviceAsync(ct).ConfigureAwait(false) && !ct.IsCancellationRequested)
            {
                // Solo termina el bucle si la SALIDA del slate tuvo éxito; si el rebuild falló (la señal volvió a
                // caer entre el sondeo y la reconstrucción), sigue sondeando en vez de abandonar. (Auditoría N3.)
                if (await ExitSlateAsync().ConfigureAwait(false)) return;
            }
        }
    }

    /// <summary>Sondea el dispositivo (libre mientras hay slate) abriéndolo ~0,5 s; éxito = señal de vuelta.</summary>
    private async Task<bool> ProbeDeviceAsync(CancellationToken ct)
    {
        if (_source is null) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _locator.FfmpegPath,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (var a in _source.BuildInputArguments()) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("0.5");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null"); psi.ArgumentList.Add("-");

            using var p = Process.Start(psi);
            if (p is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // --- Alarmas (dedupe + limpieza al cambiar de stream) ---

    private void RaiseAlarm(AlarmType type, bool active)
    {
        bool changed = active ? _activeAlarms.Add(type) : _activeAlarms.Remove(type);
        if (changed) AlarmChanged?.Invoke(this, (type, active));
    }

    private void ClearDetectAlarms()
    {
        RaiseAlarm(AlarmType.VideoBlack, false);
        RaiseAlarm(AlarmType.VideoFreeze, false);
        RaiseAlarm(AlarmType.AudioSilence, false);
    }

    private void SetState(RecordingState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Tasa nominal entera (24/25/30/50/60) para la subdivisión de cuadros del timecode. Usa la tasa de
    /// salida del perfil si se fuerza una conversión; si no, la de la señal real de la fuente; 25 por
    /// defecto. 29.97→30 y 59.94→60 por redondeo (el HH:MM:SS sigue el tiempo real, no la tasa).
    /// </summary>
    private int ResolveNominalRate(RecordingProfile profile)
    {
        var fr = profile.OutputFrameRate ?? _source?.CurrentSignal.FrameRate ?? FrameRate.P25;
        int rate = (int)Math.Round(fr.Value, MidpointRounding.AwayFromZero);
        return rate > 0 ? rate : 25;
    }

    // --- Resolución del nombre de archivo (único / contador de segmento) ---

    /// <summary>
    /// Nombre de archivo único dentro de <paramref name="dir"/>: si <c>{base}.{ext}</c> ya existe, prueba
    /// «{base} 1», «{base} 2»… hasta uno libre. Cubre tanto colisiones con grabaciones previas del mismo
    /// nombre como las piezas sucesivas de una misma grabación cortada por slate/reinicio.
    /// </summary>
    private static string ResolveUniqueSingleName(string dir, string baseName, string ext)
    {
        if (!File.Exists(Path.Combine(dir, $"{baseName}.{ext}"))) return baseName;
        for (int n = 1; ; n++)
        {
            string candidate = $"{baseName} {n}";
            if (!File.Exists(Path.Combine(dir, $"{candidate}.{ext}"))) return candidate;
        }
    }

    /// <summary>Siguiente número de segmento (1-based) tras los <c>{base}_N.{ext}</c> ya presentes en el directorio.</summary>
    private static int NextSegmentNumber(string dir, string baseName, string ext)
    {
        if (!Directory.Exists(dir)) return 1;
        int max = 0;
        foreach (var f in Directory.GetFiles(dir, $"{baseName}_*.{ext}"))
            max = Math.Max(max, SplitSegment(f).Index);
        return max + 1;
    }

    private static int CompareSegment(string a, string b)
    {
        var (pa, ia) = SplitSegment(a);
        var (pb, ib) = SplitSegment(b);
        int byPrefix = string.CompareOrdinal(pa, pb);
        return byPrefix != 0 ? byPrefix : ia.CompareTo(ib);
    }

    /// <summary>Separa «…_N.ext» en (prefijo sin «_N», N). Sin sufijo numérico → (nombre completo, -1).</summary>
    private static (string Prefix, int Index) SplitSegment(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int us = name.LastIndexOf('_');
        if (us >= 0 && us < name.Length - 1 && int.TryParse(name.AsSpan(us + 1), out var n))
            return (name[..us], n);
        return (name, -1);
    }

    /// <summary>Quita caracteres no válidos para nombre de archivo (y el «%»); vacío → null (usa el por defecto).</summary>
    internal static string? SanitizeBaseName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Trim().Where(c => !InvalidNameChars.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    // Incluye «%»: aunque es válido en un nombre de archivo, el muxer `segment` lo trata como conversión (como
    // %d) y un título tipo «Descuentos 50%» generaría un patrón inválido → FFmpeg sale con error EN BUCLE y la
    // grabación segmentada no produce nada. Quitarlo en el origen mantiene coherentes el patrón y el glob. (N12.)
    private static readonly HashSet<char> InvalidNameChars = new(Path.GetInvalidFileNameChars()) { '%' };

    /// <summary>
    /// Renombra los archivos escritos en la última grabación usando <paramref name="baseName"/> como base
    /// (dedupe « 1», « 2»… para no chocar entre sí ni con grabaciones previas). Devuelve los pares
    /// (ruta antigua, ruta nueva). Pensado para DESPUÉS de detener: el nombre manual se pide al final.
    /// </summary>
    public IReadOnlyList<(string Old, string New)> RenameSessionFiles(string baseName)
    {
        var safe = SanitizeBaseName(baseName);
        var pairs = new List<(string Old, string New)>();
        if (safe is null) return pairs;

        // QUÉ archivos se renombran se fija AL ENTRAR, antes de la espera de abajo (que con un archivo grande son
        // decenas de segundos): si mientras tanto termina OTRA grabación, _completedSessionFiles pasa a ser la suya y
        // este nombre acabaría en los archivos equivocados. Con el nombre pedido por la API al detener, un sistema
        // externo que corta clips seguidos (detener+nombre → grabar → detener+nombre) lo provocaría con facilidad.
        List<string> files;
        lock (_completedSessionFiles) files = _completedSessionFiles.ToList();

        // Espera a que termine la optimización de seek (remux faststart) en vuelo ANTES de mover los archivos:
        // el remux reescribe el archivo in-place (File.Move de un temporal sobre el original) y, si se solapara
        // con el renombrado, dejaría un archivo duplicado/huérfano o un fallo de uso compartido. Para archivos
        // grandes el remux tarda; este Wait corre en un hilo de pool (no en la UI). Margen amplio; si excede,
        // se renombra igual (el archivo es válido aunque no haya quedado optimizado).
        Task[] pendingRemux;
        lock (_optimizeLock) pendingRemux = _pendingOptimizes.ToArray();
        try { if (pendingRemux.Length > 0) Task.WaitAll(pendingRemux, TimeSpan.FromMinutes(10)); }
        catch { /* cada remux ya capturó sus errores internamente */ }

        var current = new List<string>(files.Count); // cómo queda cada archivo de la sesión, renombrado o no
        foreach (var old in files) // el snapshot de la sesión terminada (N9), tomado al entrar
        {
            current.Add(old);
            if (!File.Exists(old)) continue;
            var dir = Path.GetDirectoryName(old)!;
            var ext = Path.GetExtension(old).TrimStart('.');
            var target = ResolveUniqueSingleName(dir, safe, ext);
            var newPath = Path.Combine(dir, $"{target}.{ext}");
            if (string.Equals(old, newPath, StringComparison.OrdinalIgnoreCase)) { pairs.Add((old, newPath)); continue; }
            if (TryMoveWithRetry(old, newPath)) { pairs.Add((old, newPath)); current[^1] = newPath; }
        }

        if (pairs.Count > 0)
        {
            lock (_completedSessionFiles)
            {
                // Solo si la lista sigue siendo la de ESTA sesión: si ya la reemplazó una grabación posterior, sus
                // archivos (y su «último archivo») no se tocan.
                if (_completedSessionFiles.SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
                {
                    LastOutputFile = pairs[^1].New;
                    _completedSessionFiles.Clear();
                    _completedSessionFiles.AddRange(current);
                }
            }
        }
        return pairs;
    }

    /// <summary>Mueve con reintentos cortos: al cerrar FFmpeg el handle del archivo puede tardar un instante en liberarse.</summary>
    private bool TryMoveWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(from, to); return true; }
            catch (IOException) when (attempt < 3) { System.Threading.Thread.Sleep(150); }
            catch (Exception ex) { _log.LogError(ex, "No se pudo renombrar {From} → {To}.", from, to); return false; }
        }
    }

    /// <summary>SOLO PARA TESTS: mata el proceso FFmpeg actual para simular una caída durante la grabación y
    /// verificar que la recuperación crea una PIEZA NUEVA sin truncar la anterior. No es API de producción.</summary>
    internal bool KillRecorderProcessForTest() => _supervisor?.KillCurrentProcessForTest() ?? false;

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_source is not null) _source.SignalChanged -= OnSourceSignalChanged;
        StopRecoveryProbe();
        if (_recoveryLoop is not null) { try { await _recoveryLoop.ConfigureAwait(false); } catch { /* cancelación */ } }
        _recoveryCts?.Dispose(); // ya finalizado el loop: dispone el último CTS. (#53)
        StopAwaitSignalProbe();
        if (_awaitLoop is not null) { try { await _awaitLoop.ConfigureAwait(false); } catch { /* cancelación */ } }
        _awaitCts?.Dispose(); // ídem. (#53)
        await StopSegmentScanAsync().ConfigureAwait(false);
        if (_supervisor is not null)
        {
            Detach(_supervisor); // no "recuperar" en un stop/replace nuestro (N6)
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }
        if (_sink is not null) await _sink.DisposeAsync().ConfigureAwait(false);
        // Sumideros de relevos aún pendientes: se cierran aquí (su HandoffAsync los volverá a disponer sin efecto).
        PreviewSink[] pending;
        lock (_liveSinks) { pending = _liveSinks.ToArray(); _liveSinks.Clear(); }
        foreach (var s in pending) await s.DisposeAsync().ConfigureAwait(false);
        _pacer?.Dispose();
        _gate.Dispose();
    }
}
