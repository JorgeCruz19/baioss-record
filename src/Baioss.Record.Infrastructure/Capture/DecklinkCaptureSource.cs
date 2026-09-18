using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Fuente de captura DeckLink (SDI). Ejemplo de implementación de
/// <see cref="ICaptureSource"/>: traduce la definición a argumentos FFmpeg
/// <c>-f decklink</c> y vigila la señal vía ffprobe/Decklink SDK.
/// </summary>
public sealed class DecklinkCaptureSource(InputSource definition) : ICaptureSource
{
    // Audio pedido a la tarjeta (2/8/16 o «auto») y pares elegidos, según los parámetros de la entrada.
    private readonly AudioSelection _audio = AudioSelection.FromParameters(definition.Parameters);
    // Canales con los que se abre AHORA: en «auto» empieza en 16 y baja si la tarjeta no puede (TryReduceAudioChannels).
    private int _channels = AudioSelection.FromParameters(definition.Parameters).InitialChannels;

    public InputSource Definition { get; } = definition;
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

    /// <summary>Selección de audio de esta entrada (canales pedidos y pares a grabar).</summary>
    public AudioSelection Audio => _audio;

    public int AudioChannelCount => _channels;

    public bool TryReduceAudioChannels()
    {
        // Solo en «auto»: con un recuento fijo elegido por el operador no se toca nada (el motor registra el fallo).
        if (!_audio.Auto) return false;
        int next = AudioSelection.NextLower(_channels);
        if (next == 0) return false;
        _channels = next;
        // La señal publicada refleja los canales reales, sin re-emitir SignalChanged (no cambió la presencia).
        if (CurrentSignal.State == SignalState.Locked)
            CurrentSignal = CurrentSignal with
            {
                AudioChannels = _channels,
                AudioSelectionLabel = _channels > 2 ? _audio.Describe(_channels) : null,
                AudioSelectedPairs = _channels > 2 ? _audio.SelectedPairs(_channels) : null,
            };
        return true;
    }

    public Task OpenAsync(CancellationToken ct = default)
    {
        // Marca LOCK al asignar el dispositivo (igual que DirectShow y el archivo demo), de modo que el
        // canal habilite el botón Grabar. El SDI lleva audio embebido y el demuxer decklink siempre
        // expone una pista de audio (silencio si la fuente no la trae), así que se declara audio para
        // habilitar medidores y grabar la pista.
        //
        // LIMITACIÓN CONOCIDA (lock optimista FIJO): DeckLink es un dispositivo EXCLUSIVO; FFmpeg abre el
        // driver/tarjeta UNA sola vez. A diferencia de NDI —que tiene un receptor propio capaz de medir la
        // ausencia de frames y reportar presencia (patrón C3)—, aquí no hay forma de sondear la señal sin un
        // segundo proceso que compita por el dispositivo. En consecuencia: (1) SignalChanged NO se vuelve a
        // emitir tras OpenAsync; (2) la pérdida de señal SDI en caliente NO se detecta de forma proactiva: solo
        // la capta el watchdog del motor (negros/congelados → carta de ajuste a los ~15 s). Sin hardware DeckLink
        // no es validable un sondeo en vivo, así que se DOCUMENTA la limitación en lugar de añadir código no
        // comprobable. (Auditoría 24/7, #33.)
        // Etiqueta legible del modo elegido (p. ej. "1920×1080 · 59.94i") para mostrarla en el preview.
        Definition.Parameters.TryGetValue("format_label", out var label);
        CurrentSignal = new SignalInfo(SignalState.Locked,
            Definition.ExpectedResolution, Definition.ExpectedFrameRate,
            Definition.ExpectedAudioLayout, HasAudio: true, Timecode: null, Bitrate: null,
            FormatLabel: string.IsNullOrWhiteSpace(label) ? null : label,
            AudioChannels: _channels,
            // Solo cuando hay algo que elegir (más de un estéreo): con 2 canales la etiqueta de siempre basta.
            AudioSelectionLabel: _channels > 2 ? _audio.Describe(_channels) : null,
            AudioSelectedPairs: _channels > 2 ? _audio.SelectedPairs(_channels) : null);
        SignalChanged?.Invoke(this, CurrentSignal);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments()
    {
        var args = new List<string> { "-f", "decklink" };
        // draw_bars=false: el demuxer decklink DIBUJA barras SMPTE por defecto (draw_bars=true) mientras no hay
        // una señal válida —incluida la RE-SINCRONIZACIÓN de ~1 s al ABRIR el dispositivo—. Como iniciar/detener
        // la grabación REEMPLAZA el proceso FFmpeg (preview→grabación→preview), cada arranque REABRE la tarjeta y
        // esa ventana de sync rellenaba con barras → se GRABABAN unas décimas de barras al INICIO del archivo (y
        // se veían en el preview al detener). No es ningún «clip de respaldo»: es FFmpeg dibujando barras. Al
        // desactivarlo, la grabación empieza en el primer frame REAL; la pérdida de señal EN CALIENTE la sigue
        // cubriendo el watchdog del motor (negros/congelados → carta de ajuste), no estas barras crudas. (#33.)
        args.AddRange(new[] { "-draw_bars", "false" });
        // Modo de vídeo elegido (si no, autodetección).
        if (Definition.Parameters.TryGetValue("format_code", out var fmt))
            args.AddRange(new[] { "-format_code", fmt });
        // Canales de audio que se piden a la tarjeta (2, 8 o 16: FFmpeg no admite otros valores). Sin esta opción el
        // demuxer captura SOLO 2 —el par 1-2 del SDI— y el resto del audio embebido se pierde en silencio.
        args.AddRange(new[] { "-channels", _channels.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", Definition.Uri ?? Definition.Name });
        return args;
    }

    /// <summary>
    /// Propaga un cambio de señal (presencia/ausencia/formato). SIN CONSUMIDOR ACTIVO hoy: existe para el día
    /// que se implemente un sondeo fiable de presencia DeckLink. Por ahora <see cref="OpenAsync"/> marca un
    /// lock optimista y esto NO se llama (el dispositivo es exclusivo y no admite sondeo concurrente). (#33.)
    /// </summary>
    internal void RaiseSignal(SignalInfo info)
    {
        CurrentSignal = info;
        SignalChanged?.Invoke(this, info);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Fábrica que registra el soporte DeckLink en el sistema de captura.</summary>
public sealed class DecklinkCaptureSourceFactory : ICaptureSourceFactory
{
    public bool CanHandle(InputType type) => type is InputType.DecklinkSdi;
    public ICaptureSource Create(InputSource definition) => new DecklinkCaptureSource(definition);
}
