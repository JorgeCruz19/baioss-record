using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Streaming;

/// <summary>
/// Publica un destino de streaming. La grabación y el streaming simultáneo
/// comparten un único proceso FFmpeg mediante el muxer <c>tee</c>, pero este
/// puerto permite también re-streaming independiente desde un archivo o fuente.
/// </summary>
public interface IStreamingPublisher : IAsyncDisposable
{
    StreamTarget Target { get; }
    bool IsActive { get; }

    /// <summary>Construye la rama de salida para el muxer tee (protocolo + URL + parámetros).</summary>
    string BuildTeeBranch();

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public interface IStreamingPublisherFactory
{
    IStreamingPublisher Create(StreamTarget target);
}
