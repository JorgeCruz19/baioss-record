using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
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
    IReadOnlyList<AudioMeter>? Audio = null,
    IReadOnlyList<ChannelAlarm>? Alarms = null,
    StorageInfo? Storage = null,
    // InputName: nombre de la ENTRADA (fuente) asignada al canal, p. ej. «Clip A», «DeckLink — Mini Recorder»,
    // «NDI — OBS». Para mostrar qué entrada está activa en la UI (panel y gestor de entradas).
    string? InputName = null);

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

    /// <summary>
    /// Inicia la grabación. <paramref name="recordingName"/> es el nombre base del archivo (sin extensión):
    /// el que elige el operador en una grabación manual o el derivado de la programación
    /// (<c>dd-MM-yyyy_Título</c>). <c>null</c> = nombre por defecto <c>{canal}_{fecha_hora}</c>.
    /// </summary>
    Task StartRecordingAsync(Guid profileId, string? @operator, string? recordingName = null, CancellationToken ct = default);
    Task StopRecordingAsync(CancellationToken ct = default);

    /// <summary>Activa el modo continuo 24/7 con watchdog y auto-recuperación.</summary>
    Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>
/// Implementado por los motores cuyo perfil de grabación (formato, tamaño, códec, bitrate, audio)
/// puede elegir el operador antes de grabar. La UI lo edita; el motor graba con el perfil vigente.
/// </summary>
public interface IConfigurableRecording
{
    /// <summary>Perfil con el que se grabará al pulsar REC. Editable solo cuando el canal está inactivo.</summary>
    RecordingProfile Profile { get; set; }

    /// <summary>Carpeta de destino donde se escriben las grabaciones del canal.</summary>
    string OutputDirectory { get; set; }
}

/// <summary>
/// Implementado por los motores cuyo nombre de archivo se decide al DETENER (grabación manual): tras
/// parar, renombra la grabación recién terminada al nombre que elija el operador, con dedupe « 1», « 2»…
/// </summary>
public interface IPostRecordingRename
{
    /// <summary>
    /// Renombra los archivos de la última grabación terminada usando <paramref name="baseName"/> como base
    /// (con dedupe si ya existe). Devuelve la nueva ruta principal, o <c>null</c> si no había nada que renombrar.
    /// </summary>
    Task<string?> RenameLastRecordingAsync(string baseName, CancellationToken ct = default);
}

/// <summary>Registro de los canales activos del despliegue (A, B, …).</summary>
public interface IChannelManager
{
    IReadOnlyCollection<IChannelEngine> Channels { get; }
    IChannelEngine Get(Guid channelId);
    bool TryGet(Guid channelId, out IChannelEngine? engine);
}
