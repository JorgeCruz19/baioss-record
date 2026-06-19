using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Genera la línea de comandos FFmpeg que produciría un <see cref="RecordingProfile"/>, con una
/// entrada y una salida simbólicas. Sirve para mostrar al usuario "qué hará" un preset sin grabar.
/// </summary>
public static class FfmpegCommandPreview
{
    public static string Build(RecordingProfile profile, string inputLabel = "<entrada>", string ffmpegExe = "ffmpeg")
    {
        var args = new FfmpegArgumentBuilder()
            .From(new PlaceholderSource(inputLabel))
            .Using(profile)
            .ForChannel("CH")
            .ToDirectory("<salida>")
            .ProxyToDirectory("<salida>")
            .Build();

        return ffmpegExe + " " + string.Join(' ', args.Select(Quote));
    }

    private static string Quote(string arg) =>
        arg.AsSpan().IndexOfAny(" []|;'") >= 0 ? $"\"{arg}\"" : arg;

    /// <summary>Fuente simbólica: solo aporta el "-i &lt;entrada&gt;" para la vista previa del comando.</summary>
    private sealed class PlaceholderSource(string label) : ICaptureSource
    {
        public InputSource Definition { get; } = new() { Name = "preview", Type = InputType.File, Uri = label };
        public SignalInfo CurrentSignal => SignalInfo.None;
        public event EventHandler<SignalInfo>? SignalChanged { add { } remove { } }
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<string> BuildInputArguments() => new[] { "-i", label };
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
