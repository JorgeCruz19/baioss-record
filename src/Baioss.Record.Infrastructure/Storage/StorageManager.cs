using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Domain.Events;
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
    private readonly IEventBus? _bus; // auditoría de la retención al EventLog (opcional: null en tests)

    public StorageManager(IRecordingSessionRepository sessions, IClock clock, ILogger<StorageManager> log, IEventBus? bus = null)
    {
        _sessions = sessions;
        _clock = clock;
        _log = log;
        _bus = bus;
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
            handled += await PurgeSessionAsync(session, archive, policy.ArchivePath, policy.Action,
                $"expirada (fin hace > {policy.RetentionDays} días)", ct).ConfigureAwait(false);

        if (handled > 0)
            _log.LogInformation("Retención canal {Channel}: {Count} archivo(s) {Verb} (más de {Days} días).",
                policy.ChannelId, handled, archive ? "archivados" : "borrados", policy.RetentionDays);
    }

    /// <summary>
    /// Retención por ESPACIO (Fase 2): mientras el volumen tenga MENOS libres que el objetivo (el MAYOR entre
    /// <paramref name="minFreeGB"/> GB y <paramref name="minFreePercent"/>% del total), borra/archiva las
    /// grabaciones NO protegidas más ANTIGUAS (por fin, en CUALQUIER canal) hasta alcanzarlo o quedarse sin
    /// candidatas. Respeta protección + archivos en uso. Off si ambos objetivos son 0. Devuelve archivos tratados.
    /// </summary>
    public async Task<int> EnforceFreeSpaceAsync(string volume, double minFreeGB, int minFreePercent,
        RetentionAction action, string? archivePath, CancellationToken ct = default)
    {
        var (free, total) = ReadDrive(volume);
        long target = ComputeFreeTarget(total, minFreeGB, minFreePercent);
        if (target <= 0 || total <= 0 || free >= target) return 0; // desactivado, sin medida, o ya hay sitio

        bool archive = action == RetentionAction.Archive && !string.IsNullOrWhiteSpace(archivePath);
        int totalHandled = 0;
        for (int round = 0; round < 1000; round++) // tope de seguridad contra bucle
        {
            if (ReadDrive(volume).Free >= target) break;
            var candidates = await _sessions.GetPurgeCandidatesAsync(20, ct).ConfigureAwait(false);
            if (candidates.Count == 0) break; // no queda nada finalizado y NO protegido que borrar

            int handledThisRound = 0;
            foreach (var session in candidates)
            {
                if (ReadDrive(volume).Free >= target) break;
                handledThisRound += await PurgeSessionAsync(session, archive, archivePath, action,
                    "liberar espacio (retención por espacio)", ct).ConfigureAwait(false);
            }
            totalHandled += handledThisRound;
            if (handledThisRound == 0) break; // no se pudo liberar nada (todo en uso/error) → no insistir en vano
        }

        if (ReadDrive(volume).Free < target)
            _log.LogWarning("Retención por espacio: NO se alcanzó el objetivo ({Target:N0} bytes libres). " +
                "¿Demasiado material protegido o en uso?", target);
        else if (totalHandled > 0)
            _log.LogInformation("Retención por espacio: {Count} archivo(s) {Verb} para mantener {Target:N0} bytes libres.",
                totalHandled, archive ? "archivados" : "borrados", target);
        return totalHandled;
    }

    /// <summary>Objetivo de bytes libres = el MAYOR entre N GB y N% del total (0 = desactivado). Pura, testeable. (Fase 2.)</summary>
    internal static long ComputeFreeTarget(long totalBytes, double minFreeGB, int minFreePercent)
    {
        long target = 0;
        if (minFreeGB > 0) target = Math.Max(target, (long)(minFreeGB * 1024 * 1024 * 1024));
        if (minFreePercent > 0 && totalBytes > 0) target = Math.Max(target, (long)(totalBytes * (minFreePercent / 100.0)));
        return target;
    }

    /// <summary>
    /// Borra o archiva los archivos de UNA sesión (respetando protección y archivos en uso) y, solo si TODOS se
    /// trataron, retira la sesión de la BD. Audita al EventLog. Devuelve cuántos archivos trató. Lo comparten la
    /// retención por DÍAS y la retención por ESPACIO. (Fase 1/2.)
    /// </summary>
    private async Task<int> PurgeSessionAsync(RecordingSession session, bool archive, string? archivePath,
        RetentionAction action, string reason, CancellationToken ct)
    {
        // Guarda de PROTECCIÓN (defensa en profundidad; las consultas ya las excluyen): nunca se toca una protegida.
        if (session.Protection != RecordingProtection.None) return 0;

        bool allDone = true;
        int handled = 0;
        foreach (var seg in session.Segments)
        {
            if (string.IsNullOrEmpty(seg.FilePath)) continue;
            try
            {
                if (!File.Exists(seg.FilePath)) continue; // ya no está: nada que liberar
                // VALIDACIÓN DE SEGURIDAD: no tocar un archivo EN USO (grabándose/exportándose/reproduciéndose o
                // bloqueado). Se omite y se reintenta; se audita. (Fase 1.)
                if (IsFileInUse(seg.FilePath))
                {
                    allDone = false;
                    _ = _bus?.PublishAsync(new RetentionSkipped(session.ChannelId, session.Id,
                        $"archivo en uso: {Path.GetFileName(seg.FilePath)}"));
                    _log.LogInformation("Retención: «{File}» en uso; se omite (se reintentará).", seg.FilePath);
                    continue;
                }
                if (archive)
                {
                    Directory.CreateDirectory(archivePath!);
                    File.Move(seg.FilePath, Path.Combine(archivePath!, Path.GetFileName(seg.FilePath)), overwrite: true);
                }
                else File.Delete(seg.FilePath);
                handled++;
            }
            catch (Exception ex)
            {
                allDone = false;
                _log.LogWarning(ex, "Retención: no se pudo {Action} «{File}».", action, seg.FilePath);
            }
        }

        if (handled > 0)
            _ = _bus?.PublishAsync(new RecordingPurged(session.ChannelId, session.Id, handled, action, reason));

        // Solo se retira la sesión si TODOS sus archivos se trataron (no se pierde la referencia a uno que quedó).
        if (allDone)
        {
            try { await _sessions.RemoveAsync(session.Id, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Retención: no se pudo retirar la sesión {Id} de la BD.", session.Id); }
        }
        return handled;
    }

    /// <summary>Lee (libres, total) del volumen. Best-effort; en error devuelve (long.MaxValue, 0) → NUNCA dispara la
    /// retención por espacio (seguro: no borra si no puede medir). UNC no soportado aquí (DriveInfo). (Fase 2.)</summary>
    private static (long Free, long Total) ReadDrive(string volume)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(volume) ?? volume);
            if (d.IsReady) return (d.AvailableFreeSpace, d.TotalSize);
        }
        catch { /* UNC / no lista: sin medida */ }
        return (long.MaxValue, 0);
    }

    /// <summary>¿El archivo está EN USO (grabándose/exportándose/reproduciéndose o bloqueado por otro proceso)?
    /// Se prueba a abrirlo en EXCLUSIVA; si no se puede, no se borra. Best-effort (hay una ventana TOCTOU, pero el
    /// propio borrado la captura como respaldo). (Gestión de almacenamiento — Fase 1.)</summary>
    internal static bool IsFileInUse(string path)
    {
        try { using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }      // no existe: nada que bloquear (subclase de IOException → antes)
        catch (DirectoryNotFoundException) { return false; } // ídem
        catch (IOException) { return true; }                 // abierto por otro proceso (grabación/exportación/reproducción)
        catch (UnauthorizedAccessException) { return true; } // permisos / solo-lectura → no borrar a ciegas
    }
}
