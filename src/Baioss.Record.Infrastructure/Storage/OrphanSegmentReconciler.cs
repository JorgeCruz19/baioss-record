using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Infrastructure.Persistence;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Reconcilia los archivos de SEGMENTO que quedaron en disco sin registro en la BD tras un corte eléctrico o
/// cierre abrupto durante una grabación SEGMENTADA. El motor solo emite (y persiste) un segmento cuando FFmpeg
/// lo CIERRA y el escaneo lo detecta; el segmento en curso —y el último ya cerrado pero aún no escaneado— no
/// llegan a la BD si el proceso muere de golpe. Sin esto, ese material existe en disco pero es invisible para
/// la app (no aparece en el historial ni en la retención). En el arranque, tras cerrar las sesiones huérfanas,
/// se escanea <c>recordings/</c> y se da de alta cada archivo no registrado, asociándolo a la sesión correcta.
///
/// <para>ASOCIACIÓN ROBUSTA POR NOMBRE (no por timestamp): los segmentos de una misma grabación comparten
/// carpeta y NOMBRE BASE y solo difieren en el sufijo numérico <c>_N</c> (p. ej. <c>Noticias_1.mp4</c>,
/// <c>Noticias_2.mp4</c>…). Un huérfano <c>Noticias_5.mp4</c> se atribuye a la sesión que ya tiene segmentos
/// hermanos registrados con ese mismo nombre base en esa carpeta. Si no hay hermano (p. ej. una grabación de
/// archivo único, sin <c>_N</c>, o una sesión que murió antes de cerrar su primer segmento) no se inventa una
/// asociación: se reporta para revisión manual. (Auditoría 24/7, #23.)</para>
/// </summary>
public static class OrphanSegmentReconciler
{
    /// <summary>Un archivo de grabación hallado en disco (datos extraídos de <see cref="FileInfo"/>).</summary>
    public readonly record struct ScannedFile(string Path, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc, long SizeBytes);

    /// <summary>Resultado del plan: segmentos a crear y archivos que no se pudieron asociar (para avisar).</summary>
    public sealed record ReconcilePlan(IReadOnlyList<Segment> ToCreate, IReadOnlyList<string> Unassociated);

    // Extensiones de contenedor que produce el motor. (.faststart.* se excluyen: son temporales de remux.)
    private static readonly string[] MediaPatterns = { "*.mp4", "*.mov" };

    // Nombre (sin extensión) terminado en «_<dígitos>» ⇒ segmento N de una grabación; el prefijo es el nombre base.
    private static readonly Regex SegmentSuffix = new(@"^(?<base>.*)_(?<n>\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Escanea <paramref name="recordingsRoot"/>, compara con la BD y da de alta los segmentos huérfanos.
    /// Best-effort: cualquier fallo se propaga al llamador, que lo registra sin impedir el arranque. Idempotente:
    /// en el siguiente arranque esos archivos YA están en la BD y no se vuelven a crear.
    /// </summary>
    public static async Task<int> ReconcileAsync(
        IDbContextFactory<BaiossDbContext> factory, string recordingsRoot, ILogger log, CancellationToken ct = default)
    {
        if (!Directory.Exists(recordingsRoot)) return 0;

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var sessionStarted = await db.Sessions.AsNoTracking()
            .Select(s => new { s.Id, s.StartedAt }).ToDictionaryAsync(s => s.Id, s => s.StartedAt, ct).ConfigureAwait(false);
        var known = await db.Segments.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        var media = new List<ScannedFile>();
        foreach (var pat in MediaPatterns)
            foreach (var f in Directory.EnumerateFiles(recordingsRoot, pat, SearchOption.AllDirectories))
            {
                if (f.Contains(".faststart.", StringComparison.OrdinalIgnoreCase)) continue; // temporal de remux
                var fi = new FileInfo(f);
                if (!fi.Exists) continue;
                media.Add(new ScannedFile(f,
                    new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero),
                    new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero), fi.Length));
            }

        var plan = Plan(known, sessionStarted, media);

        if (plan.ToCreate.Count > 0)
        {
            db.Segments.AddRange(plan.ToCreate);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            log.LogWarning("Recovery: {N} segmento(s) huérfano(s) en disco (cierre abrupto en grabación segmentada) " +
                "reconciliados y registrados en la BD.", plan.ToCreate.Count);
        }
        if (plan.Unassociated.Count > 0)
            log.LogWarning("Recovery: {N} archivo(s) de grabación en disco sin segmento hermano para asociarlos " +
                "(revisar manualmente; típico de grabación de archivo único interrumpida).", plan.Unassociated.Count);

        return plan.ToCreate.Count;
    }

    /// <summary>
    /// NÚCLEO PURO (sin disco ni BD, testeable): decide qué <see cref="Segment"/> crear para los archivos en
    /// disco que no están registrados, asociándolos por nombre base a la sesión hermana más reciente.
    /// </summary>
    /// <param name="known">Segmentos ya registrados en la BD.</param>
    /// <param name="sessionStarted">Sesiones existentes → su inicio (para elegir la más reciente y evitar FK rotas).</param>
    /// <param name="media">Archivos de grabación hallados en disco.</param>
    public static ReconcilePlan Plan(
        IReadOnlyList<Segment> known,
        IReadOnlyDictionary<Guid, DateTimeOffset> sessionStarted,
        IReadOnlyList<ScannedFile> media)
    {
        var knownPaths = new HashSet<string>(known.Select(s => Normalize(s.FilePath)), StringComparer.OrdinalIgnoreCase);

        // key (carpeta + nombre base) → sesiones candidatas (solo las que EXISTEN, para no romper la FK) con su inicio.
        var keyToSessions = new Dictionary<string, Dictionary<Guid, DateTimeOffset>>(StringComparer.OrdinalIgnoreCase);
        // sesión → mayor índice de segmento ya registrado (la numeración nueva continúa desde aquí).
        var sessionMaxIndex = new Dictionary<Guid, int>();
        foreach (var seg in known)
        {
            sessionMaxIndex[seg.SessionId] = Math.Max(sessionMaxIndex.GetValueOrDefault(seg.SessionId, -1), seg.Index);
            if (!sessionStarted.TryGetValue(seg.SessionId, out var started)) continue; // sesión borrada (retención): no candidata
            var key = KeyOf(seg.FilePath);
            if (!keyToSessions.TryGetValue(key, out var bySession)) keyToSessions[key] = bySession = new();
            bySession[seg.SessionId] = started;
        }

        // Asocia cada archivo no registrado a la sesión hermana más reciente de su key.
        var associations = new List<(ScannedFile File, Guid SessionId, int FileNumber)>();
        var unassociated = new List<string>();
        foreach (var file in media)
        {
            if (knownPaths.Contains(Normalize(file.Path))) continue; // ya registrado
            var key = KeyOf(file.Path);
            if (!keyToSessions.TryGetValue(key, out var candidates) || candidates.Count == 0)
            {
                unassociated.Add(file.Path);
                continue;
            }
            // La sesión hermana más reciente: el huérfano es el último segmento de la grabación interrumpida.
            var sessionId = candidates.OrderByDescending(kv => kv.Value).First().Key;
            associations.Add((file, sessionId, SegmentNumberOf(file.Path)));
        }

        // Asigna índices CONTINUANDO el máximo de cada sesión, en orden por número de segmento del archivo.
        var toCreate = new List<Segment>();
        foreach (var grp in associations.GroupBy(a => a.SessionId))
        {
            int next = sessionMaxIndex.GetValueOrDefault(grp.Key, -1) + 1;
            foreach (var a in grp.OrderBy(a => a.FileNumber))
            {
                var dur = a.File.ModifiedUtc - a.File.CreatedUtc;
                toCreate.Add(new Segment
                {
                    SessionId = grp.Key,
                    Index = next++,
                    FilePath = a.File.Path,
                    Status = SegmentStatus.Completed, // fMP4 fragmentado: cada segmento es independiente y reproducible
                    StartedAt = a.File.CreatedUtc,
                    EndedAt = a.File.ModifiedUtc,
                    Duration = dur > TimeSpan.Zero ? dur : TimeSpan.Zero,
                    SizeBytes = a.File.SizeBytes,
                });
            }
        }
        return new ReconcilePlan(toCreate, unassociated);
    }

    // key = carpeta (normalizada) + '|' + nombre base (sin el sufijo «_N» de segmento).
    private static string KeyOf(string filePath)
    {
        var dir = Normalize(Path.GetDirectoryName(filePath) ?? "");
        var name = Path.GetFileNameWithoutExtension(filePath);
        var m = SegmentSuffix.Match(name);
        var baseName = m.Success ? m.Groups["base"].Value : name;
        return dir + "|" + baseName;
    }

    // Número de segmento (sufijo «_N»), o -1 si el archivo no lo tiene (no debería entrar como huérfano asociado).
    private static int SegmentNumberOf(string filePath)
    {
        var m = SegmentSuffix.Match(Path.GetFileNameWithoutExtension(filePath));
        return m.Success && int.TryParse(m.Groups["n"].Value, out var n) ? n : -1;
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path).Replace('/', '\\'));
}
