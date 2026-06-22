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

    /// <summary>Nombre legible para la UI (p. ej. "Noticiero matutino").</summary>
    public string Title { get; set; } = "Grabación programada";

    /// <summary>
    /// Primera (o única) ocurrencia: fecha + hora. En repeticiones, su hora del día y su offset son la
    /// referencia de cada ocurrencia; las franjas anteriores a este instante no se disparan.
    /// </summary>
    public DateTimeOffset RunAt { get; set; }

    /// <summary>Tipo de repetición (única, diaria, semanal).</summary>
    public RecurrenceKind Recurrence { get; set; } = RecurrenceKind.Once;

    /// <summary>Días seleccionados cuando <see cref="Recurrence"/> = Weekly.</summary>
    public Weekdays Weekdays { get; set; } = Weekdays.None;

    /// <summary>Duración de la grabación (auto-stop) para una acción StartRecording. Null = sin auto-stop.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Segmentar la grabación cada N minutos (cada archivo queda completo: un fallo solo pierde el
    /// segmento en curso). Null o 0 = un único archivo. Es propio de cada grabación programada.
    /// </summary>
    public int? SegmentMinutes { get; set; }

    /// <summary>Expresión CRON opcional (reservada para automatización/API; la UI usa Recurrence/Weekdays).</summary>
    public string? CronExpression { get; set; }

    // Parámetros según la acción.
    public Guid? ProfileId { get; set; }
    public Guid? InputSourceId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Última franja ya disparada (para no repetir la misma ocurrencia).</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>
    /// Franja que el operador SALTÓ manualmente: no se (re)inicia ni se reanuda esa ocurrencia. Solo
    /// afecta a esa franja; las siguientes (diaria/semanal) se ejecutan con normalidad. Persistente, para
    /// que el salto sobreviva a un reinicio dentro de la ventana de grabación.
    /// </summary>
    public DateTimeOffset? SkippedOccurrence { get; set; }
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
