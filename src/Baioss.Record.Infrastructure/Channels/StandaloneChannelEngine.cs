using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Recording;
using Baioss.Record.Infrastructure.Preview;
using Baioss.Record.Infrastructure.Storage;

namespace Baioss.Record.Infrastructure.Channels;

/// <summary>
/// Orquestador de canal autónomo: compone una <see cref="ICaptureSource"/> con el motor de captura
/// UNIFICADO (<see cref="FfmpegChannelEngine"/>), que abre la fuente una sola vez y produce preview y
/// grabación a la vez (clave para dispositivos en vivo, que no admiten dos aperturas). La persistencia
/// (sesiones y segmentos), el bus de eventos y el monitor de señal son opcionales: si se inyectan, se
/// persiste y se publican eventos; si no, el canal sigue grabando en modo "standalone". Pensado para la
/// app de escritorio en Fase 1; la variante completa basada en repositorios es <c>ChannelEngine</c>.
/// </summary>
public sealed class StandaloneChannelEngine : IChannelEngine, IConfigurableRecording, IPostRecordingRename
{
    private readonly string _key;
    private readonly ICaptureSource _source;
    private readonly FfmpegChannelEngine _engine;
    private readonly IRecordingSessionRepository? _sessions;
    private readonly IRepository<Segment>? _segments;
    private readonly IRecordingProfileRepository? _profiles;
    private readonly IEventBus? _bus;
    private readonly ISignalMonitor? _signalMonitor;
    private readonly ILogger<StandaloneChannelEngine>? _log;
    private readonly DiskSpaceGuard? _diskGuard;

    private RecordingSession? _session;

    // Segmentos persistidos de la sesión en curso + sus tareas de persistencia: para renombrar de forma
    // CONSISTENTE (que la BD no quede con el nombre temporal) cuando el operador nombra al detener.
    private readonly object _renameLock = new();
    private readonly List<Segment> _sessionSegments = new();
    private readonly List<Task> _pendingPersists = new();

    private double _peakL = -60, _peakR = -60;
    private IReadOnlyList<AudioMeter> _audio = new[] { AudioMeter.Silent, AudioMeter.Silent };

    // Saturación: racha de frames perdidos → alarma FramesDropped (ver DropAlarmTracker). Se reinicia en
    // cada start/stop para no arrastrar el conteo de una grabación a otra.
    private readonly DropAlarmTracker _drops = new();

    // Alarmas activas del canal (negro/congelado/silencio/slate/disco) y estado del almacenamiento.
    private readonly object _alarmLock = new();
    private readonly Dictionary<AlarmType, ChannelAlarm> _alarms = new();
    private StorageInfo _storage = StorageInfo.Unknown;
    private volatile bool _autoStopping;

    // Serializa Start/Stop del canal: la UI, el scheduler y el auto-stop por disco pueden invocarlos a la vez.
    // Sin esto, dos transiciones concurrentes corrompían _session y el wiring del DiskSpaceGuard. (Auditoría A5/A9/#31.)
    private readonly SemaphoreSlim _transition = new(1, 1);

    public StandaloneChannelEngine(
        string key,
        ICaptureSource source,
        RecordingProfile profile,
        FfmpegChannelEngine engine,
        Guid? channelId = null,
        IRecordingSessionRepository? sessions = null,
        IRepository<Segment>? segments = null,
        IEventBus? bus = null,
        ISignalMonitor? signalMonitor = null,
        ILogger<StandaloneChannelEngine>? log = null,
        IRecordingProfileRepository? profiles = null,
        DiskSpaceGuard? diskGuard = null)
    {
        ChannelId = channelId ?? Guid.NewGuid();
        _key = key;
        _source = source;
        Profile = profile;
        _engine = engine;
        _sessions = sessions;
        _segments = segments;
        _profiles = profiles;
        _bus = bus;
        _signalMonitor = signalMonitor;
        _log = log;
        _diskGuard = diskGuard;

        _engine.StateChanged += (_, _) => Raise();
        _engine.StatsUpdated += OnStats;
        _engine.AudioPeaksUpdated += OnAudioLevels;
        _engine.SegmentClosed += OnSegmentClosed;
        _engine.AlarmChanged += OnEngineAlarm;
        _source.SignalChanged += (_, _) => Raise();

        // Arranca la vigilancia de señal (publica lock/pérdida/silencio en el bus).
        _ = _signalMonitor?.WatchAsync(_source);
    }

    public Guid ChannelId { get; }

    /// <summary>Perfil de grabación vigente (formato, tamaño, códec, bitrate, audio). Lo edita la UI.</summary>
    public RecordingProfile Profile { get; set; }

    /// <summary>Carpeta de destino de las grabaciones (raíz; el motor crea una subcarpeta por canal).</summary>
    public string OutputDirectory
    {
        get => _engine.OutputRoot;
        set => _engine.OutputRoot = value;
    }

    public ChannelStatus Status
    {
        get
        {
            ChannelAlarm[] alarms;
            lock (_alarmLock) alarms = _alarms.Values.ToArray();
            return new(ChannelId, _key, _engine.State, _source.CurrentSignal, _engine.Stats, _session?.Id, _audio, alarms, _storage);
        }
    }

    public event EventHandler<ChannelStatus>? StatusChanged;

    public async Task BindSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        await _source.OpenAsync(ct);
        Raise();
    }

    public Task StartPreviewAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task StartRecordingAsync(Guid profileId, string? @operator, string? recordingName = null, CancellationToken ct = default)
    {
        // Serializa con Stop y con el auto-stop por disco; rechaza el doble START. (Auditoría 24/7, A5/A9/#13.)
        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A9: la UI ya impide el doble START (CanStart), pero la API/scheduler llaman directo. Arrancar una
            // 2ª grabación huérfanaría la sesión en curso (sin EndedAt) y vaciaría _sessionFiles → archivo previo
            // sin renombrar. Mejor rechazar con un error claro (que la API traduce a conflicto).
            if (_engine.State is RecordingState.Recording or RecordingState.Starting)
                throw new InvalidOperationException($"El canal {_key} ya está grabando.");
            await StartRecordingCoreAsync(profileId, @operator, recordingName, ct).ConfigureAwait(false);
        }
        finally { _transition.Release(); }
    }

    private async Task StartRecordingCoreAsync(Guid profileId, string? @operator, string? recordingName, CancellationToken ct)
    {
        lock (_renameLock) { _sessionSegments.Clear(); _pendingPersists.Clear(); }
        var profile = Profile; // el perfil elegido por el operador en la UI

        // Pre-vuelo: valida perfil y destino ANTES de crear/persistir nada, para no dejar una sesión a
        // medias. Lo bloqueante lanza (lo muestra la UI); lo no bloqueante (disco/señal) solo se registra.
        Preflight(profile, recordingName);
        _drops.Reset();

        var signal = _source.CurrentSignal;
        var session = new RecordingSession
        {
            ChannelId = ChannelId,
            ProfileId = profile.Id,
            InputSourceId = _source.Definition.Id,
            StartedAt = DateTimeOffset.UtcNow,
            State = RecordingState.Recording,
            Operator = @operator,
            Resolution = profile.TargetResolution ?? signal.Resolution,
            FrameRate = signal.FrameRate,
            AudioLayout = profile.AudioLayout,
            VideoCodec = profile.VideoCodec,
            AudioCodec = profile.AudioCodec,
        };
        _session = session;

        // Persiste el perfil usado (queda como última configuración del canal entre reinicios).
        if (_profiles is not null)
        {
            try { await _profiles.UpdateAsync(profile, ct); }
            catch (Exception ex) { _log?.LogError(ex, "No se pudo persistir el perfil {ProfileId}.", profile.Id); }
        }

        if (_sessions is not null)
        {
            try { await _sessions.AddAsync(session, ct); }
            catch (Exception ex) { _log?.LogError(ex, "No se pudo persistir la sesión {SessionId}.", session.Id); }
        }

        // Arma la grabación en el proceso de captura (el preview sigue sin interrumpirse).
        await _engine.StartRecordingAsync(session.Id, profile, recordingName, ct);

        // Vigilancia de disco: estima el tiempo restante con el ritmo de datos REAL (telemetría) y, si
        // no hay aún, con el bitrate del perfil. El crítico detiene la grabación antes de llenar el disco.
        _autoStopping = false;
        if (_diskGuard is not null)
        {
            _diskGuard.Updated += OnDiskUpdated;
            _diskGuard.Start(() => _engine.OutputRoot, () =>
            {
                long real = _engine.Stats.Bitrate.BitsPerSecond / 8;
                return real > 0 ? real : FallbackBytesPerSecond(profile);
            });
        }

        if (_bus is not null)
            await _bus.PublishAsync(new RecordingStarted(ChannelId, session.Id, @operator), ct);

        Raise();
    }

    /// <summary>Ritmo de datos estimado (bytes/s) a partir del perfil, mientras no hay telemetría real.</summary>
    private static long FallbackBytesPerSecond(RecordingProfile p)
        => ((p.MaxBitrate ?? p.VideoBitrate).BitsPerSecond + p.AudioBitrate.BitsPerSecond) / 8;

    /// <summary>
    /// Comprobaciones PREVIAS a grabar. Lo que IMPIDE grabar lanza (perfil inválido, destino no escribible)
    /// para que la UI avise y no se cree una sesión a medias; lo de MARGEN (disco ajustado, señal no
    /// bloqueada) solo se registra, porque la grabación aún puede ser válida.
    /// </summary>
    private void Preflight(RecordingProfile profile, string? recordingName)
    {
        // 1) Perfil coherente (bitrate/GOP/resolución/calidad/audio).
        if (!RecordingProfileValidator.IsValid(profile, out var errors))
            throw new InvalidOperationException("Perfil de grabación inválido: " + string.Join(" ", errors));

        // 2) Carpeta de destino escribible (existe y admite crear/borrar un archivo).
        var dir = Path.Combine(_engine.OutputRoot, _key);

        // 2b) Longitud de ruta (Windows ~260): un nombre de grabación larguísimo haría fallar a FFmpeg en
        // SILENCIO al abrir el archivo. Se reserva margen para la extensión, el dedupe « N» y el contador de
        // segmento «_NNN». Solo aplica al nombre manual/programado; el nombre por defecto es corto.
        if (!string.IsNullOrWhiteSpace(recordingName) && Path.Combine(dir, recordingName.Trim()).Length > 240)
            throw new InvalidOperationException(
                "El nombre de la grabación es demasiado largo para la ruta de destino (límite ~260 caracteres de Windows). Acorta el nombre o usa una carpeta de destino más corta.");
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"No se puede escribir en la carpeta de grabación «{dir}»: {ex.Message}", ex);
        }

        // 3) Margen de disco al arranque (no bloquea: el DiskSpaceGuard vigila el resto y detiene en crítico).
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            long bps = FallbackBytesPerSecond(profile);
            if (root is not null && bps > 0)
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    double minutes = drive.AvailableFreeSpace / (double)bps / 60.0;
                    if (minutes < 5)
                        _log?.LogWarning("Canal {Key}: poco margen de disco al iniciar (~{Min:0.0} min al bitrate del perfil).", _key, minutes);
                }
            }
        }
        catch { /* el cálculo de margen es best-effort */ }

        // 4) Señal presente (la UI ya exige lock para grabar; cubre la ruta de API/programada).
        if (_source.CurrentSignal.State != SignalState.Locked)
            _log?.LogWarning("Canal {Key}: iniciando grabación sin señal bloqueada (estado {State}).", _key, _source.CurrentSignal.State);
    }

    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        // Serializa con Start y con un Stop concurrente (auto-stop por disco / scheduler / manual); idempotente
        // si ya está detenido, para no publicar RecordingStopped dos veces ni tocar _session a null repetido.
        await _transition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is null && _engine.State is RecordingState.Idle) return; // ya detenido: no-op
            await StopRecordingCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _transition.Release(); }
    }

    private async Task StopRecordingCoreAsync(CancellationToken ct)
    {
        if (_diskGuard is not null)
        {
            _diskGuard.Updated -= OnDiskUpdated;
            await _diskGuard.StopAsync();
        }
        await _engine.StopRecordingAsync(ct);

        // Las alarmas operativas de la grabación dejan de aplicar al detener (RecordingUnverified NO: avisa
        // de un archivo dañado y debe persistir hasta la próxima grabación).
        SetAlarm(AlarmType.DiskLow, false);
        SetAlarm(AlarmType.DiskCritical, false);
        SetAlarm(AlarmType.FramesDropped, false);
        _drops.Reset();
        _storage = StorageInfo.Unknown;

        if (_session is not null)
        {
            _session.EndedAt = DateTimeOffset.UtcNow;
            _session.State = RecordingState.Idle;
            if (_sessions is not null)
            {
                try { await _sessions.UpdateAsync(_session, ct); }
                catch (Exception ex) { _log?.LogError(ex, "No se pudo actualizar la sesión {SessionId}.", _session.Id); }
            }
            if (_bus is not null)
                await _bus.PublishAsync(new RecordingStopped(ChannelId, _session.Id, _session.Duration), ct);
        }

        _session = null;
        _peakL = _peakR = -60;
        Raise();
    }

    public Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    private void OnSegmentClosed(object? sender, Segment segment)
    {
        // Registra el segmento y su tarea de persistencia (no fire-and-forget): al renombrar al detener se
        // esperan estas tareas antes de corregir la ruta en la BD, evitando una carrera Add/Update.
        lock (_renameLock) { _sessionSegments.Add(segment); _pendingPersists.Add(PersistSegmentAsync(segment)); }
    }

    private async Task PersistSegmentAsync(Segment segment)
    {
        try
        {
            if (_segments is not null) await _segments.AddAsync(segment);
            if (_bus is not null)
                await _bus.PublishAsync(new SegmentCompleted(segment.SessionId, segment.Index, segment.FilePath, segment.SizeBytes));
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "No se pudo persistir el segmento {FilePath}.", segment.FilePath);
        }
    }

    /// <summary>
    /// Renombra la última grabación terminada (grabación manual: el nombre se pide al DETENER). Mueve los
    /// archivos en segundo plano y corrige la ruta de los segmentos persistidos. Devuelve la nueva ruta
    /// principal, o null si no había nada que renombrar.
    /// </summary>
    public async Task<string?> RenameLastRecordingAsync(string baseName, CancellationToken ct = default)
    {
        // File.Move (con reintentos) no debe bloquear el hilo de UI desde el que se llama.
        var pairs = await Task.Run(() => _engine.RenameSessionFiles(baseName), ct).ConfigureAwait(false);
        if (pairs.Count == 0) return null;

        // Espera la persistencia en vuelo y corrige la ruta en la BD (que no quede el nombre temporal).
        Task[] pending;
        List<Segment> segs;
        lock (_renameLock) { pending = _pendingPersists.ToArray(); segs = _sessionSegments.ToList(); }
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* ya registrado en PersistSegmentAsync */ }

        if (_segments is not null)
        {
            var map = pairs.ToDictionary(p => p.Old, p => p.New, StringComparer.OrdinalIgnoreCase);
            foreach (var seg in segs)
                if (map.TryGetValue(seg.FilePath, out var np))
                {
                    seg.FilePath = np;
                    try { await _segments.UpdateAsync(seg, ct).ConfigureAwait(false); }
                    catch (Exception ex) { _log?.LogError(ex, "No se pudo actualizar la ruta del segmento {Id}.", seg.Id); }
                }
        }
        return pairs[^1].New;
    }

    // --- Alarmas del motor (negro/congelado/silencio/slate) ---

    private void OnEngineAlarm(object? sender, (AlarmType Type, bool Active) e)
    {
        SetAlarm(e.Type, e.Active);

        // La carta de ajuste (slate) refleja pérdida/recuperación de señal: se publica al bus.
        if (e.Type == AlarmType.Slate && _bus is not null && _session is { } s)
        {
            IDomainEvent evt = e.Active ? new SignalLost(ChannelId) : new RecordingRecovered(ChannelId, s.Id, 1);
            _ = _bus.PublishAsync(evt);
        }
    }

    private void SetAlarm(AlarmType type, bool active)
    {
        bool changed;
        lock (_alarmLock)
        {
            if (active) { changed = !_alarms.ContainsKey(type); _alarms[type] = new ChannelAlarm(type, AlarmMessage(type), DateTimeOffset.UtcNow); }
            else changed = _alarms.Remove(type);
        }
        if (changed) Raise();
    }

    private static string AlarmMessage(AlarmType t) => t switch
    {
        AlarmType.SignalLoss => "Sin señal de entrada",
        AlarmType.VideoBlack => "Imagen en negro",
        AlarmType.VideoFreeze => "Imagen congelada",
        AlarmType.AudioSilence => "Silencio de audio",
        AlarmType.DiskLow => "Espacio en disco bajo",
        AlarmType.DiskCritical => "Disco casi lleno",
        AlarmType.Slate => "Sin señal — grabando carta de ajuste",
        AlarmType.EncoderFallback => "Codificador por GPU no disponible — grabando con codificador alternativo",
        AlarmType.FramesDropped => "Frames perdidos — el equipo no da abasto (CPU/GPU/disco)",
        AlarmType.RecordingUnverified => "Grabación sin verificar — el archivo podría estar dañado",
        _ => t.ToString(),
    };

    // --- Guarda de disco ---

    private void OnDiskUpdated(object? sender, (DiskLevel Level, StorageInfo Info) e)
    {
        _storage = e.Info;
        SetAlarm(AlarmType.DiskLow, e.Level == DiskLevel.Low);
        SetAlarm(AlarmType.DiskCritical, e.Level == DiskLevel.Critical);
        Raise(); // refresca el "tiempo restante" aunque no cambie el nivel

        if (e.Level != DiskLevel.Ok && _bus is not null)
            _ = _bus.PublishAsync(new StorageLow(ChannelId, e.Info.FreeBytes, e.Info.EstimatedRemaining ?? TimeSpan.Zero));

        // Crítico → detener ordenadamente (en otra tarea: no se puede parar la guarda desde su propio hilo).
        if (e.Level == DiskLevel.Critical && !_autoStopping)
        {
            _autoStopping = true;
            _log?.LogWarning("Canal {Key}: disco crítico ({Free:N0} bytes libres); deteniendo la grabación para no corromper el archivo.", _key, e.Info.FreeBytes);
            _ = Task.Run(async () =>
            {
                try { await StopRecordingAsync(); }
                catch (Exception ex) { _log?.LogError(ex, "Auto-stop por disco falló en el canal {Key}.", _key); }
            });
        }
    }

    /// <summary>
    /// Telemetría del encoder: vigila los FRAMES PERDIDOS para alarmar ante saturación sostenida (la causa
    /// típica de degradación al grabar varios canales). Solo durante la grabación; en preview los descartes
    /// no corrompen nada. Refresca la UI en cualquier caso.
    /// </summary>
    private void OnStats(object? sender, RecorderStats stats)
    {
        if (_engine.State is RecordingState.Recording)
            SetAlarm(AlarmType.FramesDropped, _drops.Update(stats.DroppedFrames));
        Raise();
    }

    private void OnAudioLevels(object? sender, (double Left, double Right) lr)
    {
        _peakL = Math.Max(lr.Left, _peakL - 1.2); // peak-hold con decaimiento
        _peakR = Math.Max(lr.Right, _peakR - 1.2);
        _audio = new[]
        {
            new AudioMeter(_peakL, lr.Left, _peakL > -1),
            new AudioMeter(_peakR, lr.Right, _peakR > -1),
        };
        Raise();
    }

    private void Raise() => StatusChanged?.Invoke(this, Status);

    public async ValueTask DisposeAsync()
    {
        if (_diskGuard is not null) await _diskGuard.DisposeAsync();
        if (_signalMonitor is not null) await _signalMonitor.DisposeAsync();
        await _engine.DisposeAsync();
        await _source.DisposeAsync();
    }
}
