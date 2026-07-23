using System.Collections.Generic;

namespace Baioss.Record.Application.Storage;

/// <summary>Salud del almacenamiento del volumen de grabación, para el indicador GLOBAL de la UI. (Fase 4b.)</summary>
public enum StorageHealth
{
    /// <summary>Aún no medido.</summary>
    Unknown,
    /// <summary>Espacio holgado.</summary>
    Ok,
    /// <summary>Aviso: ocupado ≥ umbral de aviso.</summary>
    Warning,
    /// <summary>Crítico: ocupado ≥ umbral crítico.</summary>
    Critical,
    /// <summary>Emergencia: ocupado ≥ umbral de emergencia.</summary>
    Emergency,
}

/// <summary>Estado de UN disco de grabación (para el desglose por disco del indicador/tooltip). (Multi-disco.)</summary>
public sealed record VolumeStatus(string Label, long FreeBytes, long TotalBytes, double UsedPercent, StorageHealth Health);

/// <summary>
/// Instantánea del estado del almacenamiento de grabación para el indicador GLOBAL de la UI (visible TAMBIÉN
/// en reposo). La produce el coordinador de emergencia, que vigila los volúmenes de forma continua. Con VARIOS
/// discos de destino (un canal por disco, o mezcla), los campos «planos» (Free/Total/UsedPercent/Health) reflejan
/// el disco MÁS crítico; <see cref="VolumeCount"/> dice cuántos discos se vigilan, <see cref="WorstLabel"/> cuál es
/// el peor, y <see cref="Volumes"/> el desglose de TODOS (peor primero) para el tooltip.
/// </summary>
public sealed record StorageSnapshot(long FreeBytes, long TotalBytes, double UsedPercent, StorageHealth Health,
    bool IsEmergency, int VolumeCount = 1, string? WorstLabel = null, IReadOnlyList<VolumeStatus>? Volumes = null)
{
    public double FreeGiB => FreeBytes / 1_073_741_824d;

    /// <summary>Sin medir todavía (o volumen ilegible): la UI muestra «—».</summary>
    public static readonly StorageSnapshot Unknown = new(0, 0, 0, StorageHealth.Unknown, false);
}

/// <summary>
/// Provee el estado del almacenamiento del volumen de grabación a la UI: la instantánea actual y un evento
/// cuando cambia (cada sondeo del coordinador). Lo implementa <c>StorageEmergencyCoordinator</c>. (Fase 4b.)
/// </summary>
public interface IStorageStatusProvider
{
    /// <summary>Última instantánea conocida (o <see cref="StorageSnapshot.Unknown"/> si aún no se midió).</summary>
    StorageSnapshot Current { get; }

    /// <summary>Se eleva en cada sondeo del volumen (hilo de fondo: el consumidor debe marshalar a su hilo de UI).</summary>
    event EventHandler<StorageSnapshot>? Changed;
}
