using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.Application.UseCases.Queries;

/// <summary>Devuelve el estado consolidado de un canal (señal, grabación, telemetría).</summary>
public sealed record GetChannelStatusQuery(Guid ChannelId) : IQuery<ChannelStatus>;

public sealed class GetChannelStatusHandler(IChannelManager channels)
    : IQueryHandler<GetChannelStatusQuery, ChannelStatus>
{
    public Task<ChannelStatus> HandleAsync(GetChannelStatusQuery query, CancellationToken ct = default)
        => Task.FromResult(channels.Get(query.ChannelId).Status);
}
