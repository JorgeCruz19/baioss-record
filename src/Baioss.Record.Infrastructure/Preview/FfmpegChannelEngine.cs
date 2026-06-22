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

    private ICaptureSource? _source;
    private RecordingProfile? _baseProfile;
    private RecordingProfile? _recordProfile;
    private string _channelKey = "A";

    private FfmpegProcessSupervisor? _supervisor;
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private int _port;

    private RecordingState _state = RecordingState.Idle;
    private Guid _sessionId;
    private int _segmentIndex;
    private string? _recordFile;
    private DateTimeOffset _recordStart;

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
    private CancellationTokenSource? _segScanCts;
    private Task? _segScanLoop;

    // Carta de ajuste (slate) ante pérdida de señal + sondeo de recuperación del dispositivo.
    private volatile bool _slate;
    private volatile bool _slatePending;
    private CancellationTokenSource? _recoveryCts;
    private Task? _recoveryLoop;

    // Alarmas activas (dedupe: solo se notifican transiciones).
    private readonly HashSet<AlarmType> _activeAlarms = new();
    private volatile bool _disposed;

    public FfmpegChannelEngine(IFfmpegLocator locator, ILogger log)
    {
        _locator = locator;
        _log = log;
    }

    public int FrameWidth { get; init; } = 640;
    public int FrameHeight { get; init; } = 360;

    /// <summary>Raíz de grabación; se crea una subcarpeta por canal.</summary>
    public string OutputRoot { get; set; } = "recordings";

    public RecordingState State => _state;
    public RecorderStats Stats { get; private set; } = RecorderStats.Empty;
    public string? LastOutputFile { get; private set; }

    public event EventHandler<PreviewFrame>? FrameReady;
    public event EventHandler<(double Left, double Right)>? AudioPeaksUpdated;
    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<RecorderStats>? StatsUpdated;
    public event EventHandler<Segment>? SegmentClosed;

    /// <summary>Transición de alarma del canal (negro/congelado/silencio/slate): tipo y si pasa a activa.</summary>
    public event EventHandler<(AlarmType Type, bool Active)>? AlarmChanged;

    /// <summary>True mientras el canal rellena con carta de ajuste por pérdida de señal.</summary>
    public bool IsSlate => _slate;

    /// <summary>Arranca la captura SIEMPRE-ACTIVA (preview + medidores) sobre la fuente del canal.</summary>
    public async Task StartPreviewAsync(ICaptureSource source, RecordingProfile baseProfile, string channelKey, CancellationToken ct = default)
    {
        _source = source;
        _baseProfile = baseProfile;
        _channelKey = channelKey;

        // Servidor TCP loopback para recibir los frames de preview del proceso FFmpeg.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptCts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_acceptCts.Token), _acceptCts.Token);

        await ReplaceProcessAsync(recording: false, slate: false, ct).ConfigureAwait(false);
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
            _slate = false; _slatePending = false;
            _recordStart = DateTimeOffset.UtcNow;
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
                _segDir = Path.Combine(OutputRoot, _channelKey);
                _segGlob = $"{_recordBaseName ?? _channelKey}_*.{ext}";
                Directory.CreateDirectory(_segDir);
                foreach (var f in Directory.GetFiles(_segDir, _segGlob)) _emitted.Add(f);
            }

            SetState(RecordingState.Starting);
            await ReplaceProcessAsync(recording: true, slate: false, ct).ConfigureAwait(false);

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
            await ReplaceProcessAsync(recording: false, slate: false, ct).ConfigureAwait(false); // dispone el de grabación → flush/moov (y emite el archivo único)
            if (_segmented) ScanSegments(includeNewest: true); // emite los segmentos restantes, incluido el último ya finalizado
            RaiseAlarm(AlarmType.Slate, false);
            _recordProfile = null;
            _segmented = false;
            Stats = RecorderStats.Empty;
            SetState(RecordingState.Idle);
        }
        finally { _gate.Release(); }
    }

    private async Task ReplaceProcessAsync(bool recording, bool slate, CancellationToken ct)
    {
        // Dispone el proceso anterior (su cierre ordenado envía 'q' y finaliza el archivo si grababa).
        if (_supervisor is not null)
        {
            _supervisor.Restarted -= OnSupervisorRestarted;
            await _supervisor.DisposeAsync().ConfigureAwait(false);
            _supervisor = null;
        }

        // El stream cambió: cualquier negro/congelado/silencio detectado pertenecía al proceso anterior.
        ClearDetectAlarms();

        // Modo archivo único: el proceso que se acaba de cerrar dejó su archivo finalizado en disco →
        // emítelo como segmento (en modo segmentado lo hace el escaneo del directorio, _recordFile es null).
        if (_recordFile is not null)
        {
            EmitSegmentFile(_recordFile);
            _recordFile = null;
        }

        var profile = recording ? _recordProfile! : (_baseProfile ?? _recordProfile!);

        // El parser deriva el HH:MM:SS del tiempo real, pero necesita la tasa nominal para los cuadros
        // (FF) y para no asumir 25 fps fijos. Prioridad: tasa de salida del perfil → señal de la fuente.
        _parser.NominalRate = ResolveNominalRate(profile);

        var dir = Path.Combine(OutputRoot, _channelKey);
        if (recording) Directory.CreateDirectory(dir);

        var builder = new FfmpegArgumentBuilder()
            .From(_source!).Using(profile).ForChannel(_channelKey)
            .ToDirectory(dir).WithPreviewSink($"tcp://127.0.0.1:{_port}");

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

        _supervisor = new FfmpegProcessSupervisor(_locator.FfmpegPath, _log) { StallTimeout = TimeSpan.FromSeconds(15) };
        _supervisor.ProgressLine += OnProgress;
        _supervisor.LogLine += OnLog;
        _supervisor.Restarted += OnSupervisorRestarted;
        await _supervisor.StartAsync(args, ct).ConfigureAwait(false);
    }

    // El proceso FFmpeg se conecta como cliente al servidor TCP; aquí leemos los frames BGRA y, al
    // reiniciar el proceso (alternar grabación / respawn), re-aceptamos la nueva conexión.
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                await using var stream = client.GetStream();
                await ReadFramesAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Preview TCP: reintentando aceptación."); }
        }
    }

    private async Task ReadFramesAsync(NetworkStream stream, CancellationToken ct)
    {
        int stride = FrameWidth * 4;
        int frameSize = stride * FrameHeight;
        var buffer = new byte[frameSize];
        while (!ct.IsCancellationRequested)
        {
            int read = 0;
            while (read < frameSize)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, frameSize - read), ct).ConfigureAwait(false);
                if (n == 0) return; // el proceso cerró la conexión (reinicio) → volver a aceptar
                read += n;
            }
            FrameReady?.Invoke(this, new PreviewFrame((byte[])buffer.Clone(), FrameWidth, FrameHeight, stride));
        }
    }

    private void OnProgress(object? sender, string line)
    {
        var stats = _parser.Feed(line);
        if (stats is null) return;
        Stats = stats;
        StatsUpdated?.Invoke(this, stats);
    }

    private void OnLog(object? sender, string line)
    {
        // Alarmas de análisis: negro/congelado/silencio. Cada marca de FFmpeg → transición de alarma.
        if (FfmpegDetectParser.Parse(line) is { } d) { RaiseAlarm(d.Type, d.Active); return; }

        // Niveles de audio del filtro ebur128: "… FTPK: -16.6 -16.9 dBFS …" (1 mono, 2 estéreo).
        int ftpk = line.IndexOf("FTPK:", StringComparison.Ordinal);
        if (ftpk < 0) return;

        var toks = line[(ftpk + 5)..].TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length >= 1 && double.TryParse(toks[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var l))
        {
            double r = toks.Length >= 2 && double.TryParse(toks[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var rr) ? rr : l;
            AudioPeaksUpdated?.Invoke(this, (l, r));
        }
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

    private void EmitSegmentFile(string path)
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
    /// El supervisor reinició el proceso por un fallo de la entrada (señal perdida / dispositivo colgado).
    /// Si el perfil lo pide y estamos grabando sin slate, pasa a barras para no romper la grabación. Se
    /// despacha a otra tarea: NO se puede disponer el supervisor desde su propio hilo de evento.
    /// </summary>
    private void OnSupervisorRestarted(object? sender, int restartCount)
    {
        if (_disposed || _slate || _slatePending) return;
        if (_state is not (RecordingState.Recording or RecordingState.Starting)) return;
        if (_recordProfile?.SlateOnSignalLoss != true) return;

        _slatePending = true;
        _log.LogWarning("Canal {Key}: la entrada falló durante la grabación (reinicio #{N}); pasando a carta de ajuste.", _channelKey, restartCount);
        _ = Task.Run(EnterSlateAsync);
    }

    private async Task EnterSlateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_slate || _state is not (RecordingState.Recording or RecordingState.Starting)) return;
            _slate = true;
            await ReplaceProcessAsync(recording: true, slate: true, CancellationToken.None).ConfigureAwait(false);
            RaiseAlarm(AlarmType.Slate, true);
            StartRecoveryProbe();
        }
        catch (Exception ex) { _log.LogError(ex, "Canal {Key}: error al entrar en carta de ajuste.", _channelKey); }
        finally { _slatePending = false; _gate.Release(); }
    }

    private async Task ExitSlateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_slate || _state is not (RecordingState.Recording or RecordingState.Starting)) return;
            _slate = false;
            await ReplaceProcessAsync(recording: true, slate: false, CancellationToken.None).ConfigureAwait(false);
            RaiseAlarm(AlarmType.Slate, false);
            _log.LogInformation("Canal {Key}: señal recuperada; reanudando la fuente.", _channelKey);
        }
        catch (Exception ex) { _log.LogError(ex, "Canal {Key}: error al salir de carta de ajuste.", _channelKey); }
        finally { _gate.Release(); }
    }

    private void StartRecoveryProbe()
    {
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

            if (await ProbeDeviceAsync(ct).ConfigureAwait(false) && !ct.IsCancellationRequested)
            {
                _ = ExitSlateAsync();
                return;
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

    /// <summary>Quita caracteres no válidos para nombre de archivo; vacío → null (usa el nombre por defecto).</summary>
    private static string? SanitizeBaseName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Trim().Where(c => !InvalidNameChars.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static readonly HashSet<char> InvalidNameChars = new(Path.GetInvalidFileNameChars());

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

        foreach (var old in _sessionFiles.ToList())
        {
            if (!File.Exists(old)) continue;
            var dir = Path.GetDirectoryName(old)!;
            var ext = Path.GetExtension(old).TrimStart('.');
            var target = ResolveUniqueSingleName(dir, safe, ext);
            var newPath = Path.Combine(dir, $"{target}.{ext}");
            if (string.Equals(old, newPath, StringComparison.OrdinalIgnoreCase)) { pairs.Add((old, newPath)); continue; }
            if (TryMoveWithRetry(old, newPath)) pairs.Add((old, newPath));
        }

        if (pairs.Count > 0)
        {
            LastOutputFile = pairs[^1].New;
            _sessionFiles.Clear();
            _sessionFiles.AddRange(pairs.Select(p => p.New));
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

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        StopRecoveryProbe();
        if (_recoveryLoop is not null) { try { await _recoveryLoop.ConfigureAwait(false); } catch { /* cancelación */ } }
        await StopSegmentScanAsync().ConfigureAwait(false);
        if (_supervisor is not null)
        {
            _supervisor.Restarted -= OnSupervisorRestarted;
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }
        if (_acceptCts is not null) await _acceptCts.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* cancelación esperada */ }
        }
        try { _listener?.Stop(); } catch { /* noop */ }
        _acceptCts?.Dispose();
        _gate.Dispose();
    }
}
