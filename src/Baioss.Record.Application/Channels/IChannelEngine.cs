using Baioss.Record.Domain;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Recording;

namespace Baioss.Record.Application.Channels;

/// <summary>Nivel de audio de un canal físico para medidores VU/Peak (en dBFS).</summary>
public sealed record AudioMeter(double PeakDb, double RmsDb, bool Clipping)
{
    public static readonly AudioMeter Silent = new(-60, -60, false);
}

/// <summary>Vista consolidada del estado de un canal para UI/API.</summary>
public sealed record ChannelStatus(
    Guid ChannelId,
    string Key,
    RecordingState RecordingState,
    SignalInfo Signal,
    RecorderStats Stats,
    Guid? SessionId,
    IReadOnlyList<AudioMeter>? Audio = null);

/// <summary>
/// Orquestador de un canal. Compone captura + monitor de señal + grabación +
/// preview + streaming en una unidad coherente y aislada del resto de canales.
/// Es la "máquina" que coordina los puertos; cada canal tiene su propia instancia,
/// su propio proceso FFmpeg y su propio watchdog, garantizando independencia total.
/// </summary>
public interface IChannelEngine : IAsyncDisposable
{
    Guid ChannelId { get; }
    ChannelStatus Status { get; }
    event EventHandler<ChannelStatus>? StatusChanged;

    Task BindSourceAsync(Guid sourceId, CancellationToken ct = default);
    Task StartPreviewAsync(CancellationToken ct = default);

    Task StartRecordingAsync(Guid profileId, string? @operator, CancellationToken ct = default);
    Task StopRecordingAsync(CancellationToken ct = default);
    Task PauseRecordingAsync(CancellationToken ct = default);
    Task ResumeRecordingAsync(CancellationToken ct = default);

    /// <summary>Activa el modo continuo 24/7 con watchdog y auto-recuperación.</summary>
    Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>Registro de los canales activos del despliegue (A, B, …).</summary>
public interface IChannelManager
{
    IReadOnlyCollection<IChannelEngine> Channels { get; }
    IChannelEngine Get(Guid channelId);
    bool TryGet(Guid channelId, out IChannelEngine? engine);
}
