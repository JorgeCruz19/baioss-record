using Baioss.Record.Domain;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Configuración de la retención automática global (opt-in, sección «Retention» de appsettings). Por
/// defecto está DESHABILITADA: nunca se borra nada sin que el operador lo active explícitamente, para no
/// perder grabaciones por sorpresa.
/// </summary>
public sealed class RetentionOptions
{
    /// <summary>Si false (por defecto), no se aplica retención global automática.</summary>
    public bool Enabled { get; set; }

    /// <summary>Días a conservar; las grabaciones cuya grabación terminó hace más se borran/archivan. ≤0 = no borrar.</summary>
    public int Days { get; set; } = 30;

    /// <summary>Borrar (por defecto) o mover a <see cref="ArchivePath"/>.</summary>
    public RetentionAction Action { get; set; } = RetentionAction.Delete;

    /// <summary>Carpeta destino cuando <see cref="Action"/> = Archive.</summary>
    public string? ArchivePath { get; set; }

    /// <summary>Cada cuántas horas revisar (mínimo 1). Ignorado si <see cref="IntervalMinutes"/> &gt; 0.</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Frecuencia FINA en minutos (15/30…); si &gt; 0 TIENE PRIORIDAD sobre <see cref="IntervalHours"/>
    /// (se acota a un mínimo de 5 min para no martillear el disco). 0 = usar horas. (Fase 2.)</summary>
    public int IntervalMinutes { get; set; }

    /// <summary>Retención por ESPACIO: mantener al menos estos GB libres en el volumen de grabación (0 = desactivado). (Fase 2.)</summary>
    public double MinFreeGB { get; set; }

    /// <summary>Retención por ESPACIO: mantener al menos este % libre del volumen (0 = desactivado). Se COMBINA con
    /// <see cref="MinFreeGB"/> (gana el objetivo mayor). (Fase 2.)</summary>
    public int MinFreePercent { get; set; }
}
