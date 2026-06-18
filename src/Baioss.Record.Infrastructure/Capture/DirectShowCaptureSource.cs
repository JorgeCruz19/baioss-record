using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Fuente de captura DirectShow (webcams y capturadoras USB en Windows). El nombre del
/// dispositivo de video va en <see cref="InputSource.Uri"/>; el de audio en
/// <c>Parameters["audio"]</c>. Enumerar con: <c>ffmpeg -list_devices true -f dshow -i dummy</c>.
/// </summary>
public sealed class DirectShowCaptureSource(InputSource definition) : ICaptureSource
{
    public InputSource Definition { get; } = definition;
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

    public Task OpenAsync(CancellationToken ct = default)
    {
        CurrentSignal = new SignalInfo(SignalState.Locked,
            Definition.ExpectedResolution, Definition.ExpectedFrameRate,
            Definition.ExpectedAudioLayout, HasAudio: true, Timecode: null, Bitrate: null);
        SignalChanged?.Invoke(this, CurrentSignal);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments()
    {
        var video = Definition.Uri ?? throw new InvalidOperationException("Falta el dispositivo de video DirectShow.");
        var spec = $"video={video}";
        if (Definition.Parameters.TryGetValue("audio", out var audio) && !string.IsNullOrWhiteSpace(audio))
            spec += $":audio={audio}";
        return new[] { "-f", "dshow", "-i", spec };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DirectShowCaptureSourceFactory : ICaptureSourceFactory
{
    public bool CanHandle(InputType type) => type is InputType.DirectShow;
    public ICaptureSource Create(InputSource definition) => new DirectShowCaptureSource(definition);
}
