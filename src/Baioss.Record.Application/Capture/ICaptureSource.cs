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
    Bitrate? Bitrate,
    // Etiqueta legible del formato de entrada (p. ej. "1920×1080 · 59.94i"); preferida en UI si está.
    string? FormatLabel = null,
    // Canales de audio que entrega la fuente (2 salvo DeckLink con más canales pedidos o NDI multicanal) y, si hay
    // algo que elegir entre ellos, la etiqueta legible de lo elegido («Par 3-4 de 8»). Para la UI y la API.
    int AudioChannels = 2,
    string? AudioSelectionLabel = null,
    // Pares (1-based) que se graban de esos canales («1,3» = canales 1-2 y 5-6); null cuando no hay nada que elegir.
    // Los medidores miden TODOS los canales capturados: con esto la UI/API marcan cuáles van al archivo.
    IReadOnlyList<int>? AudioSelectedPairs = null)
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

    /// <summary>
    /// Índice de la entrada FFmpeg de la que proviene el AUDIO. 0 cuando audio y vídeo comparten la misma
    /// entrada (dshow/decklink/archivo); 1 cuando la fuente expone el audio en una entrada <c>-i</c> aparte
    /// (NDI: vídeo en la 0, audio en la 1). El builder mapea el audio desde este índice.
    /// </summary>
    int AudioInputIndex => 0;

    /// <summary>
    /// Nº de canales de audio que entrega la entrada FFmpeg de esta fuente: 2 salvo que se pidan más (DeckLink con
    /// <c>audio_channels</c>) o que la fuente los traiga (NDI según el emisor). Con más de 2, el builder ELIGE los
    /// canales a grabar con <c>pan</c> (ver <see cref="AudioSelection"/>); con <c>-ac</c> FFmpeg los mezclaría todos.
    /// </summary>
    int AudioChannelCount => 2;

    /// <summary>
    /// El dispositivo rechazó los canales de audio pedidos («Cannot enable audio input» en DeckLink): baja un
    /// escalón (16→8→2) si la entrada lo permite (<c>audio_channels=auto</c>). True si cambió y hay que reconstruir
    /// el proceso con los argumentos nuevos.
    /// </summary>
    bool TryReduceAudioChannels() => false;

    /// <summary>
    /// True si la fuente reporta por sí misma la PÉRDIDA y la RECUPERACIÓN de señal (vía <see cref="SignalChanged"/>),
    /// de modo que el motor NO necesita SONDEAR el dispositivo para detectar la vuelta de la señal. NDI lo hace (su
    /// receptor detecta presencia); DeckLink/DirectShow/archivo NO → el motor sondea el dispositivo. Sondear una
    /// fuente NDI sería redundante y DAÑINO: abriría un ffmpeg que se conecta a los sockets del PROPIO receptor (no
    /// prueba la fuente NDI real), compitiendo con el pipeline y dando falsos positivos de recuperación. (Auditoría #39/#59.)
    /// </summary>
    bool SelfReportsRecovery => false;

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
/// <c>-format_code</c> (p. ej. "Hp50"); <see cref="Description"/> es la etiqueta legible (p. ej.
/// "1920×1080 · 50p"). Lleva además la resolución/tasa parseadas para poblar la señal del canal.</summary>
public sealed record DeviceFormat(string Code, string Description)
{
    /// <summary>Opción de autodetección (sin <c>-format_code</c>): los drivers modernos detectan la señal.</summary>
    public static readonly DeviceFormat Auto = new("", "Automático (autodetección)");

    /// <summary>Resolución del modo, si se pudo parsear de la salida de <c>-list_formats</c>.</summary>
    public Resolution? Resolution { get; init; }

    /// <summary>Tasa de cuadro CODIFICADA del modo (29.97 para 1080i59.94), no la de campos.</summary>
    public FrameRate? FrameRate { get; init; }

    /// <summary>True si el modo es entrelazado.</summary>
    public bool Interlaced { get; init; }

    /// <summary>La caja del combo cae a <c>ToString()</c>: mostramos la etiqueta legible, no el record crudo.</summary>
    public override string ToString() => Description;
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

    /// <summary>
    /// Mide unos segundos el audio embebido de un dispositivo (DeckLink) o archivo y devuelve el pico por canal, para
    /// que el instalador VEA qué pares traen sonido y elija cuál grabar. <paramref name="channels"/> = 2, 8 o 16, o 0
    /// para probar 16→8→2 hasta que la tarjeta acepte. <paramref name="formatCode"/> = modo SDI elegido en la fila (si
    /// el operador fijó uno es porque la autodetección no le sirve: la medida debe abrir la tarjeta igual que la
    /// captura). <c>null</c> si no se pudo medir (dispositivo en uso, sin FFmpeg…).
    /// Requiere el dispositivo LIBRE: DeckLink es exclusivo y un canal que ya lo capture bloquea la medición.
    /// </summary>
    Task<AudioProbe?> MeasureAudioAsync(InputType type, string deviceId, int channels, string? formatCode = null, CancellationToken ct = default)
        => Task.FromResult<AudioProbe?>(null);
}

/// <summary>Resultado de medir el audio de un dispositivo: canales abiertos y pico (dBFS) de cada uno, en orden.</summary>
public sealed record AudioProbe(int Channels, IReadOnlyList<double> PeakDb)
{
    /// <summary>Por debajo de esto (dBFS) un canal cuenta como silencio a efectos de proponer pares.</summary>
    public const double SilenceDb = -60;

    /// <summary>Pico de un par (1-based): el mayor de sus dos canales; −∞ si no existe.</summary>
    public double PairPeak(int pair)
    {
        int a = (pair - 1) * 2;
        double x = a < PeakDb.Count ? PeakDb[a] : double.NegativeInfinity;
        double y = a + 1 < PeakDb.Count ? PeakDb[a + 1] : double.NegativeInfinity;
        return Math.Max(x, y);
    }

    /// <summary>Pares (1-based) con sonido en al menos uno de sus canales. Un par callado NO es un par ausente.</summary>
    public IReadOnlyList<int> ActivePairs
        => Enumerable.Range(1, Math.Max(1, PeakDb.Count / 2)).Where(p => PairPeak(p) > SilenceDb).ToList();
}

/// <summary>
/// Vigila la señal de una fuente y eleva alarmas (pérdida de señal, silencio,
/// clipping) según umbrales configurables. Publica eventos de dominio en el bus.
/// </summary>
public interface ISignalMonitor : IAsyncDisposable
{
    Task WatchAsync(ICaptureSource source, CancellationToken ct = default);
}
