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
    public InputSource Definition { get; } = definition;
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

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
            FormatLabel: string.IsNullOrWhiteSpace(label) ? null : label);
        SignalChanged?.Invoke(this, CurrentSignal);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments()
    {
        var args = new List<string> { "-f", "decklink" };
        // Parámetros típicos: formato de pixel, modo de video, canales de audio.
        if (Definition.Parameters.TryGetValue("format_code", out var fmt))
            args.AddRange(new[] { "-format_code", fmt });
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
