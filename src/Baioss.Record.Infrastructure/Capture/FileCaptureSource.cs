using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Fuente de captura desde archivo (MP4/MOV/MXF/MKV/TS…). Útil para ingest de material
/// y como fuente de prueba. Parámetros opcionales:
///  - <c>loop=1</c>     → reproduce en bucle (simula una fuente continua);
///  - <c>realtime=1</c> → lee a velocidad real (<c>-re</c>), como una entrada en vivo.
/// </summary>
public sealed class FileCaptureSource(InputSource definition) : ICaptureSource
{
    public InputSource Definition { get; } = definition;
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

    public Task OpenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Definition.Uri) || !File.Exists(Definition.Uri))
            throw new FileNotFoundException("Archivo de entrada no encontrado.", Definition.Uri);

        // En producción ffprobe poblaría resolución/fps reales; aquí basta marcar lock.
        CurrentSignal = new SignalInfo(SignalState.Locked,
            Definition.ExpectedResolution, Definition.ExpectedFrameRate,
            Definition.ExpectedAudioLayout, HasAudio: true, Timecode: null, Bitrate: null);
        SignalChanged?.Invoke(this, CurrentSignal);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments()
    {
        var args = new List<string>();
        if (Definition.Parameters.GetValueOrDefault("loop") == "1") { args.Add("-stream_loop"); args.Add("-1"); }
        if (Definition.Parameters.GetValueOrDefault("realtime") == "1") args.Add("-re");
        args.Add("-i");
        args.Add(Definition.Uri!);
        return args;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FileCaptureSourceFactory : ICaptureSourceFactory
{
    public bool CanHandle(InputType type) => type is InputType.File;
    public ICaptureSource Create(InputSource definition) => new FileCaptureSource(definition);
}
