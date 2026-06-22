using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Recording;

namespace Baioss.Record.IntegrationTests.Fakes;

/// <summary>Doble de prueba de <see cref="IChannelEngine"/> para probar la API/scheduler sin FFmpeg ni canales reales.</summary>
internal sealed class FakeChannelEngine : IChannelEngine, IConfigurableRecording
{
    private readonly string _key;
    private Guid? _sessionId;

    public FakeChannelEngine(Guid channelId, string key)
    {
        ChannelId = channelId;
        _key = key;
    }

    public Guid ChannelId { get; }
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    // IConfigurableRecording: el scheduler ajusta la segmentación del perfil antes de grabar.
    public RecordingProfile Profile { get; set; } = new() { Name = "fake" };
    public string OutputDirectory { get; set; } = "";

    public ChannelStatus Status => new(
        ChannelId, _key,
        _sessionId is null ? RecordingState.Idle : RecordingState.Recording,
        SignalInfo.None, RecorderStats.Empty, _sessionId, null);

    public event EventHandler<ChannelStatus>? StatusChanged;

    public Task BindSourceAsync(Guid sourceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task StartPreviewAsync(CancellationToken ct = default) => Task.CompletedTask;

    public string? LastRecordingName { get; private set; }

    public Task StartRecordingAsync(Guid profileId, string? @operator, string? recordingName = null, CancellationToken ct = default)
    {
        Started = true;
        StartCount++;
        LastRecordingName = recordingName;
        _sessionId = Guid.NewGuid();
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public Task StopRecordingAsync(CancellationToken ct = default)
    {
        Stopped = true;
        StopCount++;
        _sessionId = null;
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
