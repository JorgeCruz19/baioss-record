using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App.Preview;

/// <summary>
/// Registro de los motores de preview por canal. Lo puebla el composition root al construir
/// los canales y lo consultan los <see cref="ChannelViewModel"/> para enlazar su superficie de
/// render. Mantiene la propiedad del ciclo de vida (los procesos FFmpeg de preview se detienen
/// al cerrar la app).
/// </summary>
public sealed class PreviewCatalog
{
    private readonly Dictionary<Guid, FfmpegPreviewEngine> _previews = new();

    public void Add(Guid channelId, FfmpegPreviewEngine engine) => _previews[channelId] = engine;

    public FfmpegPreviewEngine? For(Guid channelId) => _previews.GetValueOrDefault(channelId);

    public IReadOnlyCollection<FfmpegPreviewEngine> All => _previews.Values;

    public async ValueTask DisposeAllAsync()
    {
        foreach (var engine in _previews.Values)
            await engine.DisposeAsync();
        _previews.Clear();
    }
}
