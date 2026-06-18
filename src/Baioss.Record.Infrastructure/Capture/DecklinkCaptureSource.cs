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
        // TODO: abrir el dispositivo vía DeckLink SDK / ffprobe y poblar CurrentSignal.
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
