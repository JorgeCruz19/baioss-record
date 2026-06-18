namespace Baioss.Record.Domain.Entities;

/// <summary>
/// Entrada inmutable del registro de eventos y auditoría. Cubre tanto eventos
/// técnicos (pérdida de señal, fallo de encoder) como de auditoría (login, acciones de operador).
/// </summary>
public sealed class EventLogEntry
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    /// <summary>Categoría de máquina (ej. "signal.lost", "recording.started", "auth.login").</summary>
    public required string Category { get; init; }

    public Guid? ChannelId { get; init; }
    public string? Operator { get; init; }
    public required string Message { get; init; }

    /// <summary>Carga útil estructurada serializada (JSON) para correlación.</summary>
    public string? PayloadJson { get; init; }
}
