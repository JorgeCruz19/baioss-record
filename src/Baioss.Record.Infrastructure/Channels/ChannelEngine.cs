using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Recording;

namespace Baioss.Record.Infrastructure.Channels;

/// <summary>
/// Orquestador de un canal: compone captura + grabación + monitor de señal y mantiene
/// la máquina de estados. Aislado por diseño — cada canal tiene su propia instancia,
/// su propio proceso FFmpeg y su propio watchdog, de modo que A y B son independientes.
/// </summary>
public sealed class ChannelEngine : IChannelEngine
{
    private readonly Channel _channel;
    private readonly IEnumerable<ICaptureSourceFactory> _captureFactories;
    private readonly IRecorderEngine _recorder;
    private readonly ISignalMonitor _signalMonitor;
    private readonly IEventBus _bus;
    private readonly IInputSourceRepository _sources;
    private readonly IRecordingProfileRepository _profiles;
    private readonly IRecordingSessionRepository _sessions;
    private readonly ILogger<ChannelEngine> _log;

    private ICaptureSource? _source;
    private RecordingSession? _session;
    private SignalInfo _signal = SignalInfo.None;

    public ChannelEngine(
        Channel channel,
        IEnumerable<ICaptureSourceFactory> captureFactories,
        IRecorderEngine recorder,
        ISignalMonitor signalMonitor,
        IEventBus bus,
        IInputSourceRepository sources,
        IRecordingProfileRepository profiles,
        IRecordingSessionRepository sessions,
        ILogger<ChannelEngine> log)
    {
        _channel = channel;
        _captureFactories = captureFactories;
        _recorder = recorder;
        _signalMonitor = signalMonitor;
        _bus = bus;
        _sources = sources;
        _profiles = profiles;
        _sessions = sessions;
        _log = log;

        _recorder.StateChanged += (_, _) => RaiseStatus();
        _recorder.StatsUpdated += (_, _) => RaiseStatus();
    }

    public Guid ChannelId => _channel.Id;

    public ChannelStatus Status => new(
        _channel.Id, _channel.Key, _recorder.State, _signal, _recorder.Stats, _session?.Id);

    public event EventHandler<ChannelStatus>? StatusChanged;

    public async Task BindSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        var def = await _sources.GetAsync(sourceId, ct)
            ?? throw new InvalidOperationException($"Fuente {sourceId} no encontrada.");

        var factory = _captureFactories.FirstOrDefault(f => f.CanHandle(def.Type))
            ?? throw new NotSupportedException($"Sin fábrica para {def.Type}.");

        if (_source is not null) await _source.DisposeAsync();
        _source = factory.Create(def);
        _source.SignalChanged += OnSignalChanged;
        await _source.OpenAsync(ct);
        _ = _signalMonitor.WatchAsync(_source, ct);
        _channel.InputSourceId = sourceId;
    }

    public Task StartPreviewAsync(CancellationToken ct = default)
        // El IPreviewEngine se adjunta aquí en la implementación completa (ver docs/04-flujos).
        => Task.CompletedTask;

    public async Task StartRecordingAsync(Guid profileId, string? @operator, CancellationToken ct = default)
    {
        if (_source is null) throw new InvalidOperationException("No hay fuente vinculada.");
        if (_signal.State != SignalState.Locked)
            _log.LogWarning("Iniciando grabación sin lock de señal en canal {Key}.", _channel.Key);

        var profile = await _profiles.GetAsync(profileId, ct)
            ?? throw new InvalidOperationException($"Perfil {profileId} no encontrado.");

        _session = new RecordingSession
        {
            ChannelId = _channel.Id,
            ProfileId = profileId,
            InputSourceId = _channel.InputSourceId!.Value,
            Operator = @operator,
            StartedAt = DateTimeOffset.UtcNow,
            State = RecordingState.Starting,
            Resolution = _signal.Resolution,
            FrameRate = _signal.FrameRate,
            StartTimecode = _signal.Timecode,
        };
        await _sessions.AddAsync(_session, ct);

        await _recorder.StartAsync(_session, profile, _source, ct);
        await _bus.PublishAsync(new RecordingStarted(_channel.Id, _session.Id, @operator), ct);
        RaiseStatus();
    }

    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        await _recorder.StopAsync(ct);
        if (_session is not null)
        {
            _session.EndedAt = DateTimeOffset.UtcNow;
            _session.EndTimecode = _signal.Timecode;
            _session.State = RecordingState.Idle;
            await _sessions.UpdateAsync(_session, ct);
            await _bus.PublishAsync(new RecordingStopped(_channel.Id, _session.Id, _session.Duration), ct);
        }
        RaiseStatus();
    }

    public Task PauseRecordingAsync(CancellationToken ct = default) => _recorder.PauseAsync(ct);
    public Task ResumeRecordingAsync(CancellationToken ct = default) => _recorder.ResumeAsync(ct);

    public Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default)
    {
        // El modo 24/7 lo gobierna el watchdog del FfmpegProcessSupervisor (reinicio
        // indefinido con backoff) más un ContinuousRecordingHostedService que re-arma
        // la sesión tras un reinicio inesperado del host (ver docs/05-resiliencia).
        _channel.ContinuousMode = enabled;
        return Task.CompletedTask;
    }

    private void OnSignalChanged(object? sender, SignalInfo info)
    {
        var previous = _signal.State;
        _signal = info;
        if (previous != info.State)
        {
            var evt = info.State == SignalState.Locked
                ? new SignalLocked(_channel.Id, info.Resolution ?? default, info.FrameRate ?? default)
                : (IDomainEvent)new SignalLost(_channel.Id);
            _ = _bus.PublishAsync(evt);
        }
        RaiseStatus();
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, Status);

    public async ValueTask DisposeAsync()
    {
        await _recorder.DisposeAsync();
        if (_source is not null) await _source.DisposeAsync();
        await _signalMonitor.DisposeAsync();
    }
}
