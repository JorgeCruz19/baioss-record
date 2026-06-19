using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Application.Capture;

/// <summary>Instantánea del estado de señal de una entrada.</summary>
public sealed record SignalInfo(
    SignalState State,
    Resolution? Resolution,
    FrameRate? FrameRate,
    AudioLayout? AudioLayout,
    bool HasAudio,
    Timecode? Timecode,
    Bitrate? Bitrate)
{
    public static readonly SignalInfo None =
        new(SignalState.NoSignal, null, null, null, false, null, null);
}

/// <summary>
/// Una fuente de captura abierta (SDI/IP/archivo/cámara). Abstrae el origen tras
/// una interfaz común; expone la señal observada y los argumentos de entrada para FFmpeg.
/// </summary>
public interface ICaptureSource : IAsyncDisposable
{
    InputSource Definition { get; }
    SignalInfo CurrentSignal { get; }
    event EventHandler<SignalInfo>? SignalChanged;

    Task OpenAsync(CancellationToken ct = default);
    Task CloseAsync(CancellationToken ct = default);

    /// <summary>Argumentos de entrada FFmpeg específicos del protocolo (ej. -f decklink -i "...").</summary>
    IReadOnlyList<string> BuildInputArguments();
}

/// <summary>
/// Fábrica por protocolo. El registro de fábricas permite añadir nuevas entradas
/// (NDI, SRT, DeckLink…) sin tocar el núcleo: principio Open/Closed.
/// </summary>
public interface ICaptureSourceFactory
{
    bool CanHandle(InputType type);
    ICaptureSource Create(InputSource definition);
}

/// <summary>Modo/formato de vídeo de un dispositivo (DeckLink): <see cref="Code"/> es el valor de
/// <c>-format_code</c> (p. ej. "Hp50"); <see cref="Description"/> es legible (p. ej. "1920x1080 50p").</summary>
public sealed record DeviceFormat(string Code, string Description)
{
    /// <summary>Opción de autodetección (sin <c>-format_code</c>): los drivers modernos detectan la señal.</summary>
    public static readonly DeviceFormat Auto = new("", "Automático (autodetección)");
}

/// <summary>Enumera dispositivos físicos disponibles (DeckLink, DirectShow, NDI sources…).</summary>
public interface IDeviceEnumerator
{
    /// <summary>Entradas de vídeo del tipo indicado, listas para asignar a un canal.</summary>
    Task<IReadOnlyList<InputSource>> DiscoverAsync(InputType type, CancellationToken ct = default);

    /// <summary>Dispositivos de audio (DirectShow) para emparejar con una entrada de vídeo. Vacío si no aplica.</summary>
    Task<IReadOnlyList<string>> DiscoverAudioDevicesAsync(InputType type, CancellationToken ct = default);

    /// <summary>Modos/formatos SDI soportados por un dispositivo (DeckLink). Vacío si no aplica o se autodetecta.</summary>
    Task<IReadOnlyList<DeviceFormat>> DiscoverFormatsAsync(InputType type, string deviceId, CancellationToken ct = default);
}

/// <summary>
/// Vigila la señal de una fuente y eleva alarmas (pérdida de señal, silencio,
/// clipping) según umbrales configurables. Publica eventos de dominio en el bus.
/// </summary>
public interface ISignalMonitor : IAsyncDisposable
{
    Task WatchAsync(ICaptureSource source, CancellationToken ct = default);
}
