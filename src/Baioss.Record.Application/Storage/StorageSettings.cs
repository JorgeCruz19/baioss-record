using Baioss.Record.Domain;

namespace Baioss.Record.Application.Storage;

/// <summary>
/// Ajustes de almacenamiento EDITABLES en caliente (retención automática + alertas por % + modo emergencia).
/// Los persiste <see cref="IStorageSettingsStore"/> (JSON) y los servicios de fondo leen el valor VIGENTE en
/// cada ciclo, así que un cambio surte efecto sin reiniciar. Seguro por defecto: retención y acciones de
/// emergencia DESACTIVADAS. (Gestión de almacenamiento — Fase 4c.)
/// </summary>
public sealed class StorageSettings
{
    // --- Retención automática (opt-in) ---
    /// <summary>Si false (por defecto), no se borra/archiva nada automáticamente.</summary>
    public bool RetentionEnabled { get; set; }
    /// <summary>Conservar N días; lo más antiguo se borra/archiva. 0 = no borrar por días.</summary>
    public int RetentionDays { get; set; } = 30;
    /// <summary>Mantener al menos estos GB libres borrando lo más antiguo NO protegido (0 = desactivado).</summary>
    public double MinFreeGB { get; set; }
    /// <summary>Mantener al menos este % libre (0 = desactivado). Se combina con <see cref="MinFreeGB"/> (gana el mayor).</summary>
    public int MinFreePercent { get; set; }
    /// <summary>Cada cuántos minutos revisar (mín. 5 si &gt; 0). 0 = usar el valor por defecto de 6 h.</summary>
    public int IntervalMinutes { get; set; } = 360;
    /// <summary>Borrar (por defecto) o archivar a <see cref="ArchivePath"/> lo expirado.</summary>
    public RetentionAction Action { get; set; } = RetentionAction.Delete;
    /// <summary>Carpeta destino cuando <see cref="Action"/> = Archive.</summary>
    public string? ArchivePath { get; set; }

    // --- Alertas por % ocupado + modo emergencia ---
    /// <summary>Ocupado ≥ este % → AVISO (indicador ámbar). 0 = desactivado.</summary>
    public int WarnPercent { get; set; } = 80;
    /// <summary>Ocupado ≥ este % → CRÍTICO. 0 = desactivado.</summary>
    public int CriticalPercent { get; set; } = 90;
    /// <summary>Ocupado ≥ este % → EMERGENCIA. 0 = desactivado.</summary>
    public int EmergencyPercent { get; set; } = 95;
    /// <summary>Opt-in: al entrar en emergencia, auto-limpia (borra lo NO protegido más antiguo).</summary>
    public bool AutoCleanupOnEmergency { get; set; }
    /// <summary>Opt-in: mientras dure la emergencia, bloquea el inicio de nuevas grabaciones.</summary>
    public bool StopNewRecordingsOnEmergency { get; set; }

    public StorageSettings Clone() => (StorageSettings)MemberwiseClone();

    /// <summary>
    /// Copia SANEADA: acota rangos (0..100 los %, mín. 5 min el intervalo, no-negativos) y fuerza un orden
    /// coherente de umbrales (aviso ≤ crítico ≤ emergencia entre los activos), para que la config nunca deje el
    /// sistema en un estado imposible. Pura y testeable. (Fase 4c.)
    /// </summary>
    public StorageSettings Sanitized()
    {
        var s = Clone();
        s.RetentionDays = Math.Max(0, s.RetentionDays);
        s.MinFreeGB = Math.Max(0, s.MinFreeGB);
        s.MinFreePercent = Math.Clamp(s.MinFreePercent, 0, 99);
        s.IntervalMinutes = s.IntervalMinutes <= 0 ? 0 : Math.Max(5, s.IntervalMinutes);
        s.WarnPercent = Math.Clamp(s.WarnPercent, 0, 100);
        s.CriticalPercent = Math.Clamp(s.CriticalPercent, 0, 100);
        s.EmergencyPercent = Math.Clamp(s.EmergencyPercent, 0, 100);
        // Orden coherente entre umbrales ACTIVOS (0 = desactivado, se ignora): aviso ≤ crítico ≤ emergencia.
        if (s.CriticalPercent > 0 && s.WarnPercent > s.CriticalPercent) s.WarnPercent = s.CriticalPercent;
        if (s.EmergencyPercent > 0 && s.CriticalPercent > s.EmergencyPercent) s.CriticalPercent = s.EmergencyPercent;
        if (s.EmergencyPercent > 0 && s.WarnPercent > s.EmergencyPercent) s.WarnPercent = s.EmergencyPercent;
        if (s.Action == RetentionAction.Archive && string.IsNullOrWhiteSpace(s.ArchivePath))
            s.Action = RetentionAction.Delete; // archivar sin carpeta no tiene sentido → borrar
        return s;
    }
}

/// <summary>
/// Almacén persistente de <see cref="StorageSettings"/> (JSON). Fuente de verdad ÚNICA de los ajustes de
/// almacenamiento: la UI/API lo editan con <see cref="Save"/> y los servicios de fondo leen <see cref="Current"/>
/// en cada ciclo. <see cref="Changed"/> avisa a los interesados (p. ej. la UI). (Fase 4c.)
/// </summary>
public interface IStorageSettingsStore
{
    /// <summary>Ajustes vigentes (una COPIA: mutarla no afecta al almacén hasta llamar a <see cref="Save"/>).</summary>
    StorageSettings Current { get; }

    /// <summary>Sanea y persiste los ajustes, y eleva <see cref="Changed"/>.</summary>
    void Save(StorageSettings settings);

    /// <summary>Se eleva tras un <see cref="Save"/> (para que la UI/servicios reaccionen).</summary>
    event EventHandler? Changed;
}
