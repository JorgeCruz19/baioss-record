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
        // Solo hay pista de audio si se emparejó un dispositivo de audio DirectShow (va en :audio=…).
        // Muchas cámaras y la "OBS Virtual Camera" son SOLO-VÍDEO; declararlo evita pedirle a FFmpeg
        // una salida de medición sin streams (que abortaría el proceso, y con él preview y grabación).
        bool hasAudio = Definition.Parameters.TryGetValue("audio", out var audio) && !string.IsNullOrWhiteSpace(audio);
        CurrentSignal = new SignalInfo(SignalState.Locked,
            Definition.ExpectedResolution, Definition.ExpectedFrameRate,
            hasAudio ? Definition.ExpectedAudioLayout : null, HasAudio: hasAudio, Timecode: null, Bitrate: null);
        SignalChanged?.Invoke(this, CurrentSignal);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments()
    {
        var video = Definition.Uri ?? throw new InvalidOperationException("Falta el dispositivo de video DirectShow.");
        var spec = $"video={video}";
        var args = new List<string> { "-f", "dshow" };
        if (Definition.Parameters.TryGetValue("audio", out var audio) && !string.IsNullOrWhiteSpace(audio))
        {
            spec += $":audio={audio}";
            // Vídeo y audio de DISPOSITIVOS distintos (p. ej. OBS Virtual Camera + micrófono de la laptop)
            // reportan timestamps de dispositivo con offsets dispares (el micro arranca con un tiempo enorme);
            // al combinarlos en un solo grafo dshow, FFmpeg RETIENE los frames de vídeo esperando alinearlos y
            // el preview se CONGELA varios segundos. Sellar AMBOS con el reloj de pared elimina el desfase y el
            // vídeo fluye desde el primer frame. Verificado: sin esto, 3 frames en 6 s; con esto, 30 fps.
            args.Add("-use_wallclock_as_timestamps"); args.Add("1");
        }
        args.Add("-i"); args.Add(spec);
        return args;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DirectShowCaptureSourceFactory : ICaptureSourceFactory
{
    public bool CanHandle(InputType type) => type is InputType.DirectShow;
    public ICaptureSource Create(InputSource definition) => new DirectShowCaptureSource(definition);
}
