using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Vigila la señal de una <see cref="ICaptureSource"/> y publica eventos de dominio en
/// el bus ante transiciones (lock / pérdida) y ausencia de audio. Cada instancia vigila
/// la fuente de un canal; <see cref="ChannelId"/> correlaciona los eventos con su canal.
/// </summary>
public sealed class SignalMonitor : ISignalMonitor
{
    private readonly IEventBus _bus;
    private readonly ILogger<SignalMonitor> _log;

    private ICaptureSource? _source;
    private SignalState _lastState = SignalState.NoSignal;
    private bool _audioAlarmRaised;

    public SignalMonitor(IEventBus bus, ILogger<SignalMonitor> log)
    {
        _bus = bus;
        _log = log;
    }

    /// <summary>Canal cuya fuente se está vigilando (para correlacionar los eventos publicados).</summary>
    public Guid ChannelId { get; set; }

    public Task WatchAsync(ICaptureSource source, CancellationToken ct = default)
    {
        _source = source;
        source.SignalChanged += OnSignalChanged;
        _lastState = SignalState.NoSignal;
        _ = EvaluateAsync(source.CurrentSignal); // estado inicial
        return Task.CompletedTask; // vigilancia dirigida por eventos; vive hasta DisposeAsync
    }

    private void OnSignalChanged(object? sender, SignalInfo info) => _ = EvaluateAsync(info);

    private async Task EvaluateAsync(SignalInfo info)
    {
        try
        {
            if (info.State != _lastState)
            {
                var previous = _lastState;
                _lastState = info.State;

                if (info.State == SignalState.Locked)
                {
                    _log.LogInformation("Canal {Channel}: señal con LOCK ({Resolution} @ {FrameRate}).",
                        ChannelId, info.Resolution, info.FrameRate);
                    await _bus.PublishAsync(new SignalLocked(ChannelId, info.Resolution ?? default, info.FrameRate ?? default));
                }
                else if (previous == SignalState.Locked)
                {
                    _log.LogWarning("Canal {Channel}: PÉRDIDA de señal ({State}).", ChannelId, info.State);
                    await _bus.PublishAsync(new SignalLost(ChannelId));
                }
            }

            // Alarma de silencio: hay imagen con lock pero la fuente no reporta audio.
            bool noAudio = info.State == SignalState.Locked && !info.HasAudio;
            if (noAudio && !_audioAlarmRaised)
            {
                _audioAlarmRaised = true;
                _log.LogWarning("Canal {Channel}: señal sin audio.", ChannelId);
                await _bus.PublishAsync(new AudioSilenceDetected(ChannelId, TimeSpan.Zero));
            }
            else if (!noAudio)
            {
                _audioAlarmRaised = false;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Canal {Channel}: error evaluando la señal.", ChannelId);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_source is not null) _source.SignalChanged -= OnSignalChanged;
        _source = null;
        return ValueTask.CompletedTask;
    }
}
