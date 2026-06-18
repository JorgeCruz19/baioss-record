using Baioss.Record.Application.Channels;

namespace Baioss.Record.Infrastructure.Channels;

/// <summary>
/// Registro de los canales del despliegue (A y B por defecto; N en configuración
/// empresarial). Cada <see cref="IChannelEngine"/> es independiente del resto.
/// </summary>
public sealed class ChannelManager : IChannelManager
{
    private readonly Dictionary<Guid, IChannelEngine> _engines;

    public ChannelManager(IEnumerable<IChannelEngine> engines)
        => _engines = engines.ToDictionary(e => e.ChannelId);

    public IReadOnlyCollection<IChannelEngine> Channels => _engines.Values;

    public IChannelEngine Get(Guid channelId) =>
        _engines.TryGetValue(channelId, out var e)
            ? e
            : throw new KeyNotFoundException($"Canal {channelId} no registrado.");

    public bool TryGet(Guid channelId, out IChannelEngine? engine)
        => _engines.TryGetValue(channelId, out engine);
}
