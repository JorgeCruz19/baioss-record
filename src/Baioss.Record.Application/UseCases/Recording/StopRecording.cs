using Baioss.Record.Application.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.Application.UseCases.Recording;

/// <summary>
/// Detiene la grabación activa de un canal y cierra la sesión. Con <paramref name="RecordingName"/>, además le pone ese
/// nombre al archivo recién terminado —lo mismo que el diálogo «¿cómo guardarla?» de la aplicación al detener una
/// grabación manual—, para que el panel web y los sistemas externos no dejen el material con el nombre temporal
/// <c>{canal}_{fecha_hora}</c>. <paramref name="Operator"/> es quién le pone el nombre (para la auditoría).
/// </summary>
public sealed record StopRecordingCommand(Guid ChannelId, string? RecordingName = null, string? Operator = null)
    : ICommand<StopRecordingResult>;

/// <summary>Qué pasó con el nombre pedido al detener.</summary>
public enum RecordingNameOutcome
{
    /// <summary>No se pidió nombre: el archivo queda con el temporal (lo de siempre).</summary>
    NotRequested,
    /// <summary>Renombrado: <see cref="StopRecordingResult.FileName"/> es el nombre final (con « 1», « 2»… si ya existía).</summary>
    Renamed,
    /// <summary>Aceptado, pero el archivo aún se está optimizando (remux): se renombrará solo al terminar.</summary>
    Pending,
    /// <summary>No se aplicó; <see cref="StopRecordingResult.Detail"/> dice por qué. La grabación se detuvo igual.</summary>
    Ignored,
}

/// <summary>Por qué no se aplicó un nombre. Texto estable (en inglés técnico) para que un cliente pueda distinguirlo.</summary>
public static class RecordingNameIgnored
{
    public const string NotRecording = "not-recording";   // el canal no estaba grabando: no hay nada que nombrar
    public const string Scheduled = "scheduled";          // grabación programada: ya se guarda como fecha_Título
    public const string Unsupported = "unsupported";      // el motor de este canal no renombra (canal simulado)
    public const string NothingRenamed = "not-renamed";   // nombre sin caracteres válidos, o el archivo no se pudo mover
}

public sealed record StopRecordingResult(RecordingNameOutcome Name, string? FileName = null, string? Detail = null)
{
    public static readonly StopRecordingResult Plain = new(RecordingNameOutcome.NotRequested);
}

/// <summary>Tipo vacío para comandos sin valor de retorno (estilo MediatR).</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

public sealed class StopRecordingHandler(IChannelManager channels)
    : ICommandHandler<StopRecordingCommand, StopRecordingResult>
{
    /// <summary>Lo que se espera al renombrado antes de contestar «pendiente». El archivo no se puede mover mientras se
    /// optimiza (remux faststart), y eso en una grabación larga son decenas de segundos: la petición HTTP no se queda
    /// colgada; el renombrado sigue solo y deja su entrada en la auditoría (<c>RecordingRenamed</c>).</summary>
    public TimeSpan RenameWait { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Tope del nombre (sin extensión). El motor ya quita los caracteres no válidos; esto evita rutas imposibles.</summary>
    public const int MaxNameLength = 120;

    public async Task<StopRecordingResult> HandleAsync(StopRecordingCommand command, CancellationToken ct = default)
    {
        var channel = channels.Get(command.ChannelId);
        // Se mira ANTES de detener: después la sesión ya no está y no se sabría si era programada, ni si había algo.
        var before = channel.Status;
        bool wasRecording = before.SessionId is not null;
        bool scheduled = before.SessionTrigger == RecordingTrigger.Scheduled;

        await channel.StopRecordingAsync(RecordingStopReason.Api, ct);

        var name = command.RecordingName?.Trim();
        if (string.IsNullOrEmpty(name)) return StopRecordingResult.Plain;
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].Trim();

        // Sin grabación en curso NO se renombra: «la última grabación» sería una anterior, quizá ya nombrada por otro.
        if (!wasRecording) return new(RecordingNameOutcome.Ignored, Detail: RecordingNameIgnored.NotRecording);
        if (scheduled) return new(RecordingNameOutcome.Ignored, Detail: RecordingNameIgnored.Scheduled);
        if (channel is not IPostRecordingRename renamer) return new(RecordingNameOutcome.Ignored, Detail: RecordingNameIgnored.Unsupported);

        // Sin el token de la petición: un cliente que se va a medias no debe dejar el renombrado a la mitad.
        var rename = renamer.RenameLastRecordingAsync(name, command.Operator, CancellationToken.None);
        var first = await Task.WhenAny(rename, Task.Delay(RenameWait, ct)).ConfigureAwait(false);
        if (first != rename)
        {
            _ = rename.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // que no quede sin observar
            return new(RecordingNameOutcome.Pending);
        }

        var path = await rename.ConfigureAwait(false);
        return path is null
            ? new(RecordingNameOutcome.Ignored, Detail: RecordingNameIgnored.NothingRenamed)
            : new(RecordingNameOutcome.Renamed, Path.GetFileName(path));
    }
}
