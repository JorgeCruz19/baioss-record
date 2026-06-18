using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.Application.UseCases.Recording;

/// <summary>Detiene la grabación activa de un canal y cierra la sesión.</summary>
public sealed record StopRecordingCommand(Guid ChannelId) : ICommand<Unit>;

/// <summary>Tipo vacío para comandos sin valor de retorno (estilo MediatR).</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

public sealed class StopRecordingHandler(IChannelManager channels)
    : ICommandHandler<StopRecordingCommand, Unit>
{
    public async Task<Unit> HandleAsync(StopRecordingCommand command, CancellationToken ct = default)
    {
        await channels.Get(command.ChannelId).StopRecordingAsync(ct);
        return Unit.Value;
    }
}
