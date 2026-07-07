namespace Baioss.Record.Application.Storage;

/// <summary>
/// Compuerta GLOBAL de almacenamiento, consultada al ARRANCAR una grabación (pre-vuelo del canal). Si el
/// volumen de grabación está en EMERGENCIA de espacio y la opción de bloqueo está activa, impide iniciar
/// grabaciones nuevas (grabar a un disco casi lleno corrompería el archivo). El estado lo mantiene el
/// coordinador global de emergencia, que vigila el volumen de forma continua (también en IDLE). Opt-in:
/// por defecto NUNCA bloquea (solo alerta). (Gestión de almacenamiento — Fase 3b.)
/// </summary>
public interface IStorageGate
{
    /// <summary>True si debe BLOQUEARSE el inicio de nuevas grabaciones (emergencia activa + opción de bloqueo habilitada).</summary>
    bool ShouldBlockNewRecordings { get; }

    /// <summary>Motivo legible del bloqueo (para el error mostrado al operador), o null si no bloquea.</summary>
    string? BlockReason { get; }
}
