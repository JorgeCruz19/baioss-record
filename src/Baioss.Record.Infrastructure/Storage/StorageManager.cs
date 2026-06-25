using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Implementación de <see cref="IStorageManager"/> sobre el sistema de archivos local.
/// Calcula espacio, estima tiempo restante por bitrate agregado y aplica retención.
/// </summary>
public sealed class StorageManager : IStorageManager
{
    private readonly IRecordingSessionRepository _sessions;
    private readonly IClock _clock;
    private readonly ILogger<StorageManager> _log;

    public StorageManager(IRecordingSessionRepository sessions, IClock clock, ILogger<StorageManager> log)
    {
        _sessions = sessions;
        _clock = clock;
        _log = log;
    }

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

    /// <summary>
    /// Borra (o archiva, si <see cref="RetentionPolicy.Action"/> = Archive) las grabaciones del canal cuya
    /// grabación terminó hace más de <see cref="RetentionPolicy.RetentionDays"/> días. Trabaja sobre la BD
    /// (fuente de verdad): por cada sesión expirada gestiona sus archivos de segmento y, solo si TODOS se
    /// pudieron tratar, retira el registro de la sesión (no se pierde la referencia a un archivo que no se
    /// pudo borrar/mover). Una grabación en curso (sin EndedAt) nunca entra. <c>RetentionDays ≤ 0</c> =
    /// conservar indefinidamente (no borra nada).
    /// </summary>
    public async Task ApplyRetentionAsync(RetentionPolicy policy, CancellationToken ct = default)
    {
        if (policy.RetentionDays <= 0) return;
        var cutoff = _clock.UtcNow - TimeSpan.FromDays(policy.RetentionDays);
        var sessions = await _sessions.GetEndedBeforeAsync(policy.ChannelId, cutoff, ct).ConfigureAwait(false);
        if (sessions.Count == 0) return;

        bool archive = policy.Action == RetentionAction.Archive && !string.IsNullOrWhiteSpace(policy.ArchivePath);
        int handled = 0;

        foreach (var session in sessions)
        {
            bool allDone = true;
            foreach (var seg in session.Segments)
            {
                if (string.IsNullOrEmpty(seg.FilePath)) continue;
                try
                {
                    if (!File.Exists(seg.FilePath)) continue; // ya no está: nada que liberar
                    if (archive)
                    {
                        Directory.CreateDirectory(policy.ArchivePath!);
                        File.Move(seg.FilePath, Path.Combine(policy.ArchivePath!, Path.GetFileName(seg.FilePath)), overwrite: true);
                    }
                    else File.Delete(seg.FilePath);
                    handled++;
                }
                catch (Exception ex)
                {
                    allDone = false;
                    _log.LogWarning(ex, "Retención: no se pudo {Action} «{File}».", policy.Action, seg.FilePath);
                }
            }

            if (allDone)
            {
                try { await _sessions.RemoveAsync(session.Id, ct).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "Retención: no se pudo retirar la sesión {Id} de la BD.", session.Id); }
            }
        }

        if (handled > 0)
            _log.LogInformation("Retención canal {Channel}: {Count} archivo(s) {Verb} (más de {Days} días).",
                policy.ChannelId, handled, archive ? "archivados" : "borrados", policy.RetentionDays);
    }
}
