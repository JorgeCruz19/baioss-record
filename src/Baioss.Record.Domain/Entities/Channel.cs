namespace Baioss.Record.Domain.Entities;

/// <summary>
/// Canal de grabación independiente (A, B, … N). El producto comercial expone dos
/// canales por defecto pero el dominio no fija el número: la cantidad es configuración
/// de despliegue, lo que mantiene la escalabilidad multicanal.
/// </summary>
public sealed class Channel
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Etiqueta corta visible en UI (ej. "A", "B", "MCR-1").</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    /// <summary>Fuente actualmente vinculada (puede cambiar por scheduler/API).</summary>
    public Guid? InputSourceId { get; set; }

    /// <summary>Perfil de grabación activo.</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>Modo de grabación continua 24/7 con watchdog y auto-recuperación.</summary>
    public bool ContinuousMode { get; set; }

    public bool Enabled { get; set; } = true;
}
