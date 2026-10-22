using Microsoft.Extensions.Logging;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Implementación de <see cref="IRecordingDeletionService"/> sobre el sistema de archivos local y la BD.
///
/// <para>Por cada grabación: primero se comprueba que NINGUNO de sus archivos está en uso (un reproductor, la
/// verificación que sigue al cierre, un remux) ni protegido contra escritura; si alguno lo está, de esa grabación no se
/// borra nada. Después se borran sus archivos uno a uno y, al final, sus filas: la de cada archivo borrado o que ya no
/// estaba y, si no queda ninguna, la de la sesión. Si un archivo falla a mitad, su fila se conserva (y con ella la
/// sesión): nunca queda en el disco un archivo que la aplicación ya no ve, ni una fila sin su sesión.</para>
///
/// <para>Un archivo que no está se da por borrado y solo se quita su registro. Si lo que falta es el DISCO o la carpeta
/// de red entera, el plan lo dice (<see cref="RecordingDeletionPlan.UnavailableLocations"/>) para que el operador lo sepa
/// antes de confirmar: el borrado hace lo mismo, pero si ese disco vuelve sus archivos seguirán en él.</para>
/// </summary>
public sealed class RecordingDeletionService : IRecordingDeletionService
{
    private readonly IRecordingSessionRepository _sessions;
    private readonly ILogger<RecordingDeletionService> _log;
    private readonly IEventBus? _bus;
    private readonly Func<IReadOnlyCollection<Guid>>? _recordingNow;

    /// <summary><paramref name="recordingNow"/>: sesiones que los canales están escribiendo ahora (<c>Status.SessionId</c>);
    /// null = solo cuenta el fin en la BD (tests, o sin canales).</summary>
    public RecordingDeletionService(IRecordingSessionRepository sessions, ILogger<RecordingDeletionService> log,
        IEventBus? bus = null, Func<IReadOnlyCollection<Guid>>? recordingNow = null)
    {
        _sessions = sessions;
        _log = log;
        _bus = bus;
        _recordingNow = recordingNow;
    }

    public IReadOnlySet<Guid> RecordingNow()
    {
        try { return _recordingNow?.Invoke().ToHashSet() ?? new HashSet<Guid>(); }
        catch (Exception ex)
        {
            // Un canal reconstruyéndose justo ahora: sin la lista, sigue valiendo la regla del fin en la BD.
            _log.LogDebug(ex, "Borrado de grabaciones: no se pudo leer qué graban los canales.");
            return new HashSet<Guid>();
        }
    }

    public RecordingDeletionPlan Plan(IReadOnlyCollection<RecordingSession> sessions)
    {
        var now = RecordingNow();
        var probe = new DiskProbe();
        int recordings = 0, found = 0, missing = 0, inProgress = 0, protectedCount = 0;
        long bytes = 0;
        foreach (var s in sessions)
        {
            if (RecordingDeletionRules.IsInProgress(s, now)) { inProgress++; continue; }
            if (RecordingDeletionRules.IsProtected(s)) { protectedCount++; continue; }
            recordings++;
            foreach (var seg in s.Segments)
            {
                if (probe.Of(seg.FilePath) == FileState.Present) { found++; bytes += LengthOr(seg.FilePath, seg.SizeBytes); }
                else missing++;
            }
        }
        return new RecordingDeletionPlan(recordings, found, bytes, missing, probe.UnreachableRoots(), inProgress, protectedCount);
    }

    public async Task<RecordingDeletionResult> DeleteAsync(IReadOnlyCollection<Guid> sessionIds, string? operatorName,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var now = RecordingNow();
        var probe = new DiskProbe();
        var issues = new List<RecordingFileIssue>();
        int recordings = 0, filesDeleted = 0, filesMissing = 0, inProgress = 0, protectedCount = 0, dbErrors = 0, done = 0;
        long freed = 0;

        foreach (var id in sessionIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            // Releída de la BD: la lista puede llevar abierta un rato (otra ventana la protegió, la retención la borró, el
            // renombrado de una recién terminada cambió sus rutas).
            var session = await _sessions.GetWithSegmentsAsync(id, ct).ConfigureAwait(false);
            if (session is null) { /* ya no existe: nada que borrar */ }
            else if (RecordingDeletionRules.IsInProgress(session, now)) inProgress++;
            else if (RecordingDeletionRules.IsProtected(session)) protectedCount++;
            else if (DeleteFiles(session, probe, issues) is { } files)
            {
                // Con los archivos ya borrados, su registro se quita entero aunque cancelen: si no, quedarían filas
                // apuntando a archivos que ya no existen.
                bool removed = false;
                try { removed = await _sessions.RemoveRecordsAsync(session.Id, files.Removable, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    dbErrors++;
                    _log.LogError(ex, "Borrado de grabaciones: se borraron los archivos de la sesión {Id} pero no su registro.", session.Id);
                }
                if (removed) recordings++;
                filesDeleted += files.Deleted;
                filesMissing += files.Missing;
                freed += files.Bytes;
                if (removed || files.Deleted > 0)
                    await PublishAsync(new RecordingDeleted(session.ChannelId, session.Id, FirstFileName(session), session.StartedAt,
                        files.Deleted, files.Bytes, files.Missing, operatorName)).ConfigureAwait(false);
            }
            progress?.Report(++done);
        }

        if (recordings > 0 || filesDeleted > 0)
            _log.LogInformation("Borrado de grabaciones por {Operator}: {Recordings} grabación(es), {Files} archivo(s) ({Bytes:N0} bytes) " +
                "y {Missing} registro(s) sin archivo.", operatorName ?? "?", recordings, filesDeleted, freed, filesMissing);
        return new RecordingDeletionResult(recordings, filesDeleted, freed, filesMissing, inProgress, protectedCount, issues, dbErrors);
    }

    /// <summary>Lo que se borró de una grabación: las filas de segmento que ya se pueden quitar, y cuántos archivos.</summary>
    private readonly record struct DeletedFiles(List<Guid> Removable, int Deleted, int Missing, long Bytes);

    /// <summary>Borra los archivos de UNA grabación. Null si alguno está en uso o sin permiso: entonces no toca ninguno.</summary>
    private DeletedFiles? DeleteFiles(RecordingSession session, DiskProbe probe, List<RecordingFileIssue> issues)
    {
        var segments = session.Segments.OrderBy(s => s.Index).ToList();
        var states = segments.Select(s => probe.Of(s.FilePath)).ToArray();

        for (int i = 0; i < segments.Count; i++)
        {
            if (states[i] != FileState.Present) continue;
            switch (ExclusiveAccess(segments[i].FilePath))
            {
                case Access.Gone: states[i] = FileState.Missing; break; // se fue entre medias: solo el registro
                case Access.InUse:
                    issues.Add(new RecordingFileIssue(Path.GetFileName(segments[i].FilePath), RecordingFileProblem.InUse));
                    return null;
                case Access.Denied:
                    issues.Add(new RecordingFileIssue(Path.GetFileName(segments[i].FilePath), RecordingFileProblem.AccessDenied));
                    return null;
            }
        }

        var removable = new List<Guid>(segments.Count);
        int deleted = 0, missing = 0;
        long bytes = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (states[i] != FileState.Present) { removable.Add(seg.Id); missing++; continue; }
            try
            {
                long size = LengthOr(seg.FilePath, seg.SizeBytes);
                File.Delete(seg.FilePath);
                removable.Add(seg.Id);
                deleted++;
                bytes += size;
            }
            catch (Exception ex)
            {
                // Se conserva su fila (y la sesión): la grabación sigue en la lista con lo que queda.
                issues.Add(new RecordingFileIssue(Path.GetFileName(seg.FilePath), RecordingFileProblem.DeleteFailed));
                _log.LogWarning(ex, "Borrado de grabaciones: no se pudo borrar «{File}»; se conserva su registro.", seg.FilePath);
            }
        }
        return new DeletedFiles(removable, deleted, missing, bytes);
    }

    private async Task PublishAsync(IDomainEvent e)
    {
        if (_bus is null) return;
        try { await _bus.PublishAsync(e, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "Borrado de grabaciones: no se pudo dejar constancia en la auditoría."); }
    }

    private static string? FirstFileName(RecordingSession s)
        => s.Segments.OrderBy(x => x.Index).Select(x => x.FilePath).FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } p
            ? Path.GetFileName(p)
            : null;

    private static long LengthOr(string path, long fallback)
    {
        try { return new FileInfo(path).Length; }
        catch { return fallback; }
    }

    private enum FileState { Present, Missing, Unreachable }

    private enum Access { Free, Gone, InUse, Denied }

    /// <summary>Como <see cref="StorageManager.IsFileInUse"/>, pero distingue «en uso» de «sin permiso», que el operador
    /// resuelve de formas distintas (cerrar el reproductor / quitar el solo lectura).</summary>
    private static Access ExclusiveAccess(string path)
    {
        try { using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return Access.Free; }
        catch (FileNotFoundException) { return Access.Gone; }      // subclases de IOException: antes
        catch (DirectoryNotFoundException) { return Access.Gone; }
        catch (UnauthorizedAccessException) { return Access.Denied; }
        catch (IOException) { return Access.InUse; }
    }

    /// <summary>
    /// ¿Está el archivo? Pregunta por cada DISCO (o recurso de red) una sola vez: si no responde, sus archivos se dan por
    /// ausentes sin preguntar por cada uno (con una carpeta de red caída, cada pregunta tarda segundos).
    /// </summary>
    private sealed class DiskProbe
    {
        private readonly Dictionary<string, bool> _roots = new(StringComparer.OrdinalIgnoreCase);

        public FileState Of(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return FileState.Missing;
            string? root;
            try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
            catch { return FileState.Missing; } // ruta malformada: no hay archivo que borrar
            if (!string.IsNullOrEmpty(root))
            {
                if (!_roots.TryGetValue(root, out bool up)) _roots[root] = up = Directory.Exists(root);
                if (!up) return FileState.Unreachable;
            }
            return File.Exists(path) ? FileState.Present : FileState.Missing;
        }

        public IReadOnlyList<string> UnreachableRoots() => _roots.Where(r => !r.Value).Select(r => r.Key).ToList();
    }
}
