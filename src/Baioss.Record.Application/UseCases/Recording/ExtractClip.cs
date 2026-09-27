using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Localization;

namespace Baioss.Record.Application.UseCases.Recording;

/// <summary>
/// Saca un clip de los últimos <paramref name="Seconds"/> segundos de la grabación EN CURSO de un canal (sin detenerla ni
/// recodificar). <paramref name="Operator"/> es quién lo pide (auditoría). Lo usan el botón «✂ Clip» de la aplicación y
/// <c>POST /channels/{id}/clip</c>.
/// </summary>
public sealed record ExtractClipCommand(Guid ChannelId, int Seconds, string? Operator = null) : ICommand<ClipResult>;

public sealed class ExtractClipHandler(IChannelManager channels) : ICommandHandler<ExtractClipCommand, ClipResult>
{
    /// <summary>Duración admitida: desde unos segundos hasta una hora (más es una grabación, no un clip).</summary>
    public const int MinSeconds = 5, MaxSeconds = 3600;

    public async Task<ClipResult> HandleAsync(ExtractClipCommand command, CancellationToken ct = default)
    {
        if (command.Seconds is < MinSeconds or > MaxSeconds)
            throw new ClipExtractionException(ClipError.InvalidDuration, Localizer.F("Clip_Err_Duration", MinSeconds, MaxSeconds));
        var channel = channels.Get(command.ChannelId);
        if (channel is not IClipExtraction clips)
            throw new ClipExtractionException(ClipError.UnsupportedContainer, Localizer.T("Clip_Err_Unsupported"));
        return await clips.ExtractClipAsync(TimeSpan.FromSeconds(command.Seconds), command.Operator, ct).ConfigureAwait(false);
    }
}
