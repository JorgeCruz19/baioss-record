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

    public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<string> BuildInputArguments() => new[] { "-i", Definition.Uri! };
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Fuerza una transición de señal y notifica a los suscriptores.</summary>
    public void Emit(SignalInfo signal)
    {
        CurrentSignal = signal;
        SignalChanged?.Invoke(this, signal);
    }
}
