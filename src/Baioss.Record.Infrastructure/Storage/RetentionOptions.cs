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

    /// <summary>Cada cuántas horas revisar (mínimo 1).</summary>
    public int IntervalHours { get; set; } = 6;
}
