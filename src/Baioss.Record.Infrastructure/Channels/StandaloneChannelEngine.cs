using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.Infrastructure.Channels;

/// <summary>
/// Orquestador de canal autónomo: compone una <see cref="ICaptureSource"/> con el motor de captura
/// UNIFICADO (<see cref="FfmpegChannelEngine"/>), que abre la fuente una sola vez y produce preview y
/// grabación a la vez (clave para dispositivos en vivo, que no admiten dos aperturas). La persistencia
/// (sesiones y segmentos), el bus de eventos y el monitor de señal son opcionales: si se inyectan, se
/// persiste y se publican eventos; si no, el canal sigue grabando en modo "standalone". Pensado para la
/// app de escritorio en Fase 1; la variante completa basada en repositorios es <c>ChannelEngine</c>.
/// </summary>
public sealed class StandaloneChannelEngine : IChannelEngine, IConfigurableRecording
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

    private RecordingSession? _session;
    private double _peakL = -60, _peakR = -60;
    private IReadOnlyList<AudioMeter> _audio = new[] { AudioMeter.Silent, AudioMeter.Silent };

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
        IRecordingProfileRepository? profiles = null)
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

        _engine.StateChanged += (_, _) => Raise();
        _engine.StatsUpdated += (_, _) => Raise();
        _engine.AudioPeaksUpdated += OnAudioLevels;
        _engine.SegmentClosed += OnSegmentClosed;
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

    public ChannelStatus Status =>
        new(ChannelId, _key, _engine.State, _source.CurrentSignal, _engine.Stats, _session?.Id, _audio);

    public event EventHandler<ChannelStatus>? StatusChanged;

    public async Task BindSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        await _source.OpenAsync(ct);
        Raise();
    }

    public Task StartPreviewAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task StartRecordingAsync(Guid profileId, string? @operator, CancellationToken ct = default)
    {
        var profile = Profile; // el perfil elegido por el operador en la UI
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
        await _engine.StartRecordingAsync(session.Id, profile, ct);

        if (_bus is not null)
            await _bus.PublishAsync(new RecordingStarted(ChannelId, session.Id, @operator), ct);

        Raise();
    }

    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        await _engine.StopRecordingAsync(ct);

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

    public Task PauseRecordingAsync(CancellationToken ct = default) => _engine.PauseAsync(ct);
    public Task ResumeRecordingAsync(CancellationToken ct = default) => _engine.ResumeAsync(ct);
    public Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    private async void OnSegmentClosed(object? sender, Segment segment)
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
        if (_signalMonitor is not null) await _signalMonitor.DisposeAsync();
        await _engine.DisposeAsync();
        await _source.DisposeAsync();
    }
}
