using System.Diagnostics;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.IntegrationTests.Fakes;

/// <summary>
/// Fuente en vivo sintética (lavfi a tiempo real: barras de prueba a 25 fps + un tono) que se auto-reporta como el relé
/// DeckLink o NDI, con la señal en manos del test: sin señal, o «rota», no deja construir el proceso del canal.
/// </summary>
internal sealed class SyntheticLiveSource : ICaptureSource
{
    private int _builds;

    private static readonly SignalInfo Locked = new(SignalState.Locked, new Resolution(320, 180), FrameRate.P25,
        AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null);

    public InputSource Definition { get; } = new() { Name = "sintética", Type = InputType.File, Uri = "lavfi" };
    public SignalInfo CurrentSignal { get; private set; } = Locked;
    public event EventHandler<SignalInfo>? SignalChanged;
    public bool SelfReportsRecovery => true;
    public bool DeliversInRealTime => true;

    /// <summary>Con señal y todo, construir el proceso falla (un fallo que no es de la señal).</summary>
    public volatile bool Broken;

    /// <summary>Veces que el motor construyó (o intentó construir) un proceso sobre la fuente.</summary>
    public int Builds => Volatile.Read(ref _builds);

    /// <summary>Cuándo se pidió cada construcción, en segundos de <see cref="Clock"/>.</summary>
    public readonly List<double> BuildTimes = new();

    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    /// <summary>Cambia la señal y lo anuncia (aunque no cambie: una fuente real también repite avisos).</summary>
    public void Set(SignalState state)
    {
        CurrentSignal = Locked with { State = state };
        SignalChanged?.Invoke(this, CurrentSignal);
    }

    public IReadOnlyList<string> BuildInputArguments()
    {
        Interlocked.Increment(ref _builds);
        lock (BuildTimes) BuildTimes.Add(Clock.Elapsed.TotalSeconds);
        if (CurrentSignal.State != SignalState.Locked) throw new InvalidOperationException("La fuente no tiene señal.");
        if (Broken) throw new InvalidOperationException("Fallo que no es de la señal.");
        return new[]
        {
            "-re", "-f", "lavfi", "-i",
            "testsrc2=size=320x180:rate=25[out0];sine=frequency=1000:sample_rate=48000[out1]",
        };
    }

    public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
