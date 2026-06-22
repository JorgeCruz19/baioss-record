using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.Application.UseCases.Recording;

/// <summary>Inicia la grabación de un canal con un perfil dado.</summary>
public sealed record StartRecordingCommand(Guid ChannelId, Guid ProfileId, string? Operator)
    : ICommand<StartRecordingResult>;

public sealed record StartRecordingResult(Guid SessionId);

public sealed class StartRecordingHandler(IChannelManager channels)
    : ICommandHandler<StartRecordingCommand, StartRecordingResult>
{
    public async Task<StartRecordingResult> HandleAsync(StartRecordingCommand command, CancellationToken ct = default)
    {
        var channel = channels.Get(command.ChannelId);
        await channel.StartRecordingAsync(command.ProfileId, command.Operator, ct: ct);
        return new StartRecordingResult(channel.Status.SessionId
            ?? throw new InvalidOperationException("El canal no devolvió una sesión activa."));
    }
}
