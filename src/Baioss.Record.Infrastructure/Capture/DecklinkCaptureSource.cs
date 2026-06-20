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
        // NOTA: es un lock optimista al asignar; la detección real de presencia/ausencia de señal y de
        // resolución/fps vía DeckLink SDK / ffprobe queda pendiente (no puede sondear un dispositivo en
        // vivo exclusivo sin chocar con el proceso de captura).
        CurrentSignal = new SignalInfo(SignalState.Locked,
            Definition.ExpectedResolution, Definition.ExpectedFrameRate,
            Definition.ExpectedAudioLayout, HasAudio: true, Timecode: null, Bitrate: null);
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
