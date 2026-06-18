using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Channels;

/// <summary>
/// Orquestador de canal autónomo (sin persistencia): compone una <see cref="ICaptureSource"/>
/// con el <see cref="FfmpegRecorderEngine"/> real para grabar de verdad. Pensado para la
/// app de escritorio en Fase 1; la variante con repositorios/eventos es <c>ChannelEngine</c>.
/// </summary>
public sealed class StandaloneChannelEngine : IChannelEngine
{
    private readonly string _key;
    private readonly ICaptureSource _source;
    private readonly RecordingProfile _profile;
    private readonly FfmpegRecorderEngine _recorder;

    private Guid? _sessionId;
    private double _peakL = -60, _peakR = -60;
    private IReadOnlyList<AudioMeter> _audio = new[] { AudioMeter.Silent, AudioMeter.Silent };

    public StandaloneChannelEngine(string key, ICaptureSource source, RecordingProfile profile, FfmpegRecorderEngine recorder)
    {
        ChannelId = Guid.NewGuid();
        _key = key;
        _source = source;
        _profile = profile;
        _recorder = recorder;
        _recorder.StateChanged += (_, _) => Raise();
        _recorder.StatsUpdated += (_, _) => Raise();
        _recorder.AudioLevelsUpdated += OnAudioLevels;
    }

    public Guid ChannelId { get; }

    public ChannelStatus Status =>
        new(ChannelId, _key, _recorder.State, _source.CurrentSignal, _recorder.Stats, _sessionId, _audio);

    public event EventHandler<ChannelStatus>? StatusChanged;

    public async Task BindSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        await _source.OpenAsync(ct);
        Raise();
    }

    public Task StartPreviewAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task StartRecordingAsync(Guid profileId, string? @operator, CancellationToken ct = default)
    {
        _sessionId = Guid.NewGuid();
        var session = new RecordingSession
        {
            Id = _sessionId.Value,
            ChannelId = ChannelId,
            ProfileId = _profile.Id,
            InputSourceId = _source.Definition.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Operator = @operator,
        };
        await _recorder.StartAsync(session, _profile, _source, ct);
        Raise();
    }

    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        await _recorder.StopAsync(ct);
        _sessionId = null;
        _peakL = _peakR = -60;
        _audio = new[] { AudioMeter.Silent, AudioMeter.Silent };
        Raise();
    }

    public Task PauseRecordingAsync(CancellationToken ct = default) => _recorder.PauseAsync(ct);
    public Task ResumeRecordingAsync(CancellationToken ct = default) => _recorder.ResumeAsync(ct);
    public Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

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
        await _recorder.DisposeAsync();
        await _source.DisposeAsync();
    }
}
