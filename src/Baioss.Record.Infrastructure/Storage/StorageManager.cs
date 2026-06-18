using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Implementación de <see cref="IStorageManager"/> sobre el sistema de archivos local.
/// Calcula espacio, estima tiempo restante por bitrate agregado y aplica retención.
/// </summary>
public sealed class StorageManager : IStorageManager
{
    public Task<StorageStatus> GetStatusAsync(string volume, CancellationToken ct = default)
    {
        var drive = new DriveInfo(Path.GetPathRoot(volume) ?? volume);
        // El bitrate agregado real lo provee el ChannelManager; aquí se asume placeholder.
        var status = new StorageStatus(
            Volume: drive.Name,
            FreeBytes: drive.AvailableFreeSpace,
            TotalBytes: drive.TotalSize,
            EstimatedRemaining: TimeSpan.Zero,
            BytesPerChannel: new Dictionary<Guid, long>());
        return Task.FromResult(status);
    }

    public TimeSpan EstimateRemaining(long freeBytes, Bitrate aggregateBitrate)
    {
        if (aggregateBitrate.BitsPerSecond <= 0) return TimeSpan.MaxValue;
        double seconds = freeBytes * 8.0 / aggregateBitrate.BitsPerSecond;
        return TimeSpan.FromSeconds(seconds);
    }

    public Task ApplyRetentionAsync(RetentionPolicy policy, CancellationToken ct = default)
    {
        // TODO: enumerar sesiones más antiguas que RetentionDays y borrar/archivar
        // de forma transaccional (primero mover/copiar, luego borrar; nunca al revés).
        return Task.CompletedTask;
    }
}
