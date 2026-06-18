using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Application.Storage;

/// <summary>Estado de almacenamiento por volumen y por canal.</summary>
public sealed record StorageStatus(
    string Volume,
    long FreeBytes,
    long TotalBytes,
    TimeSpan EstimatedRemaining,
    IReadOnlyDictionary<Guid, long> BytesPerChannel);

/// <summary>
/// Gestiona espacio, estimación de tiempo restante, retención y archivado.
/// Eleva <c>StorageLow</c> cuando el tiempo estimado cae bajo el umbral.
/// </summary>
public interface IStorageManager
{
    Task<StorageStatus> GetStatusAsync(string volume, CancellationToken ct = default);

    /// <summary>Tiempo de grabación restante dado el espacio libre y el bitrate agregado activo.</summary>
    TimeSpan EstimateRemaining(long freeBytes, Bitrate aggregateBitrate);

    /// <summary>Aplica auto-delete/archivado según la política (7/30/90 días o personalizada).</summary>
    Task ApplyRetentionAsync(RetentionPolicy policy, CancellationToken ct = default);
}
