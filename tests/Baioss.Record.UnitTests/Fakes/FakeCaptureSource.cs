using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.UnitTests.Fakes;

/// <summary>Doble de prueba de <see cref="ICaptureSource"/>: señal controlable y args fijos.</summary>
internal sealed class FakeCaptureSource : ICaptureSource
{
    public FakeCaptureSource(string uri = "C:/tmp/in.mp4")
        => Definition = new InputSource { Name = "fake", Type = InputType.File, Uri = uri };

    public InputSource Definition { get; }
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

    /// <summary>Canales de audio que «entrega» la fuente (2 por defecto; 8/16 para probar la selección multicanal).</summary>
    public int AudioChannelCount { get; set; } = 2;

    /// <summary>Veces que el motor pidió bajar los canales (la fuente falsa nunca puede).</summary>
    public int ReduceRequests { get; private set; }
    public bool TryReduceAudioChannels() { ReduceRequests++; return false; }

    public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<string> BuildInputArguments() => new[] { "-i", Definition.Uri! };
    public int PreviewFramesToSkip { get; set; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Fuerza una transición de señal y notifica a los suscriptores.</summary>
    public void Emit(SignalInfo signal)
    {
        CurrentSignal = signal;
        SignalChanged?.Invoke(this, signal);
    }
}
