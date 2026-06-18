namespace Baioss.Record.Domain.Entities;

/// <summary>
/// Trabajo programado que el Scheduler dispara por fecha/hora/calendario:
/// iniciar o detener grabación, cambiar de perfil o conmutar fuente.
/// </summary>
public sealed class ScheduledJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChannelId { get; init; }
    public required ScheduledAction Action { get; set; }

    public DateTimeOffset RunAt { get; set; }

    /// <summary>Expresión CRON opcional para recurrencia (null = ejecución única).</summary>
    public string? CronExpression { get; set; }

    // Parámetros según la acción.
    public Guid? ProfileId { get; set; }
    public Guid? InputSourceId { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
}

/// <summary>Política de retención/archivado aplicada al almacenamiento de un canal.</summary>
public sealed class RetentionPolicy
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChannelId { get; init; }
    public int RetentionDays { get; set; } = 30;
    public RetentionAction Action { get; set; } = RetentionAction.Delete;

    /// <summary>Ruta de archivado cuando Action = Archive.</summary>
    public string? ArchivePath { get; set; }
}
