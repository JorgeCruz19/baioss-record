using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App.Preview;

/// <summary>
/// Registro de la fuente de preview por canal (<see cref="IChannelPreviewSource"/>). Lo puebla el
/// composition root al construir los canales y lo consultan los <see cref="ChannelViewModel"/> para
/// enlazar su superficie de render. Mantiene referencias NO propietarias: cada fuente (el motor de
/// captura unificado) es propiedad de su canal y se dispone con él.
/// </summary>
public sealed class PreviewCatalog
{
    private readonly Dictionary<Guid, IChannelPreviewSource> _previews = new();

    public void Add(Guid channelId, IChannelPreviewSource source) => _previews[channelId] = source;

    public IChannelPreviewSource? For(Guid channelId) => _previews.GetValueOrDefault(channelId);

    /// <summary>Quita la referencia de un canal (al reasignarle la entrada). No dispone: lo hace el canal.</summary>
    public void Remove(Guid channelId) => _previews.Remove(channelId);
}
