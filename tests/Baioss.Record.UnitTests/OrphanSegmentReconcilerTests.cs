using Baioss.Record.Domain.Entities;
using Baioss.Record.Infrastructure.Storage;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Verifica el NÚCLEO PURO de reconciliación de segmentos huérfanos: un archivo en disco no registrado se
/// atribuye a la sesión HERMANA (mismo nombre base en la misma carpeta) más reciente, continuando su índice;
/// sin hermano no se inventa asociación; y una sesión inexistente (borrada por retención) nunca recibe el
/// huérfano (evita romper la FK). (Auditoría 24/7, #23.)
/// </summary>
public sealed class OrphanSegmentReconcilerTests
{
    private static readonly Guid S1 = Guid.NewGuid();
    private static readonly Guid S2 = Guid.NewGuid();

    private static readonly DateTimeOffset T0 = new(2026, 6, 20, 20, 0, 0, TimeSpan.Zero);

    private static Segment Seg(Guid session, int index, string path)
        => new() { SessionId = session, Index = index, FilePath = path };

    private static OrphanSegmentReconciler.ScannedFile File(string path)
        => new(path, T0, T0.AddMinutes(10), SizeBytes: 1_000_000);

    [Fact]
    public void ReconcilesTrailingOrphan_IntoSiblingSession_ContinuingIndex()
    {
        var known = new[]
        {
            Seg(S1, 0, @"C:\rec\A\Noticias_1.mp4"),
            Seg(S1, 1, @"C:\rec\A\Noticias_2.mp4"),
        };
        var started = new Dictionary<Guid, DateTimeOffset> { [S1] = T0 };
        var media = new[]
        {
            File(@"C:\rec\A\Noticias_1.mp4"), // ya registrado
            File(@"C:\rec\A\Noticias_2.mp4"), // ya registrado
            File(@"C:\rec\A\Noticias_3.mp4"), // HUÉRFANO (corte eléctrico)
        };

        var plan = OrphanSegmentReconciler.Plan(known, started, media);

        var created = Assert.Single(plan.ToCreate);
        Assert.Equal(S1, created.SessionId);
        Assert.Equal(2, created.Index);                       // continúa tras el índice 1
        Assert.Equal(@"C:\rec\A\Noticias_3.mp4", created.FilePath);
        Assert.Equal(SegmentStatus.Completed, created.Status);
        Assert.Empty(plan.Unassociated);
    }

    [Fact]
    public void DoesNotRecreate_AlreadyRegisteredFiles()
    {
        var known = new[] { Seg(S1, 0, @"C:\rec\A\Noticias_1.mp4") };
        var started = new Dictionary<Guid, DateTimeOffset> { [S1] = T0 };
        var media = new[] { File(@"C:\rec\A\Noticias_1.mp4") };

        var plan = OrphanSegmentReconciler.Plan(known, started, media);

        Assert.Empty(plan.ToCreate);
        Assert.Empty(plan.Unassociated);
    }

    [Fact]
    public void OrphanWithoutSibling_IsReported_NotInvented()
    {
        var known = new[] { Seg(S1, 0, @"C:\rec\A\Noticias_1.mp4") };
        var started = new Dictionary<Guid, DateTimeOffset> { [S1] = T0 };
        // Archivo único interrumpido (sin sufijo «_N») y sin hermano registrado: no se puede asociar.
        var media = new[] { File(@"C:\rec\A\Entrevista.mp4") };

        var plan = OrphanSegmentReconciler.Plan(known, started, media);

        Assert.Empty(plan.ToCreate);
        Assert.Equal(@"C:\rec\A\Entrevista.mp4", Assert.Single(plan.Unassociated));
    }

    [Fact]
    public void MultipleOrphans_GetSequentialIndices_InFileOrder()
    {
        var known = new[] { Seg(S1, 0, @"C:\rec\A\Show_1.mp4") };
        var started = new Dictionary<Guid, DateTimeOffset> { [S1] = T0 };
        var media = new[]
        {
            File(@"C:\rec\A\Show_1.mp4"),
            File(@"C:\rec\A\Show_3.mp4"), // desordenados en el array de entrada
            File(@"C:\rec\A\Show_2.mp4"),
        };

        var plan = OrphanSegmentReconciler.Plan(known, started, media);

        Assert.Equal(2, plan.ToCreate.Count);
        var byIndex = plan.ToCreate.OrderBy(s => s.Index).ToList();
        Assert.Equal((1, @"C:\rec\A\Show_2.mp4"), (byIndex[0].Index, byIndex[0].FilePath)); // _2 va antes
        Assert.Equal((2, @"C:\rec\A\Show_3.mp4"), (byIndex[1].Index, byIndex[1].FilePath)); // _3 después
    }

    [Fact]
    public void SameBaseTwoSessions_OrphanGoesToMostRecentSession()
    {
        // Dos grabaciones con el mismo nombre base en la misma carpeta (numeración continuada en disco):
        // el huérfano final pertenece a la sesión MÁS RECIENTE (la interrumpida).
        var known = new[]
        {
            Seg(S1, 0, @"C:\rec\A\Diario_1.mp4"), // sesión vieja
            Seg(S2, 0, @"C:\rec\A\Diario_2.mp4"), // sesión nueva
        };
        var started = new Dictionary<Guid, DateTimeOffset> { [S1] = T0, [S2] = T0.AddHours(1) };
        var media = new[]
        {
            File(@"C:\rec\A\Diario_1.mp4"),
            File(@"C:\rec\A\Diario_2.mp4"),
            File(@"C:\rec\A\Diario_3.mp4"), // huérfano
        };

        var plan = OrphanSegmentReconciler.Plan(known, started, media);

        var created = Assert.Single(plan.ToCreate);
        Assert.Equal(S2, created.SessionId);   // la más reciente
        Assert.Equal(1, created.Index);        // continúa tras el índice 0 de S2
    }

    [Fact]
    public void OrphanOfDeletedSession_IsNotReattached_FkSafe()
    {
        // El segmento hermano referencia una sesión que YA NO existe (borrada por retención): no es candidata,
        // así que el huérfano queda como no asociado en vez de provocar una FK rota al insertar.
        var known = new[] { Seg(S1, 0, @"C:\rec\A\Viejo_1.mp4") };
        var started = new Dictionary<Guid, DateTimeOffset>(); // S1 ausente
        var media = new[] { File(@"C:\rec\A\Viejo_2.mp4") };

        var plan = OrphanSegmentReconciler.Plan(known, started, media);

        Assert.Empty(plan.ToCreate);
        Assert.Equal(@"C:\rec\A\Viejo_2.mp4", Assert.Single(plan.Unassociated));
    }

    [Fact]
    public void RecentlyWrittenFile_IsSkipped_NotReconciled()
    {
        // Hay un hermano válido, pero el candidato se modificó hace 5 s: en el arranque puede ser el segmento
        // que una grabación REANUDADA está escribiendo justo ahora. Con «now» dentro del margen, se ignora
        // (ni se registra ni se reporta): el motor lo emitirá al cerrarlo. (Auditoría N17.)
        var known = new[] { Seg(S1, 0, @"C:\rec\A\Vivo_1.mp4") };
        var started = new Dictionary<Guid, DateTimeOffset> { [S1] = T0 };
        var now = T0.AddHours(2);
        var recent = new OrphanSegmentReconciler.ScannedFile(
            @"C:\rec\A\Vivo_2.mp4", now.AddSeconds(-30), now.AddSeconds(-5), SizeBytes: 500_000);
        var media = new[] { File(@"C:\rec\A\Vivo_1.mp4"), recent };

        var plan = OrphanSegmentReconciler.Plan(known, started, media, now, TimeSpan.FromSeconds(60));

        Assert.Empty(plan.ToCreate);
        Assert.Empty(plan.Unassociated);
    }

    [Fact]
    public void OldOrphan_IsStillReconciled_EvenWithRecencyFilterOn()
    {
        // El margen de escritura NO debe frenar a un huérfano ANTIGUO (modificado hace horas): ese sí es de una
        // grabación muerta y debe reconciliarse. (N17: el margen solo protege lo recién escrito.)
        var known = new[] { Seg(S1, 0, @"C:\rec\A\Muerto_1.mp4") };
        var started = new Dictionary<Guid, DateTimeOffset> { [S1] = T0 };
        var now = T0.AddHours(5);
        var old = new OrphanSegmentReconciler.ScannedFile(
            @"C:\rec\A\Muerto_2.mp4", T0, T0.AddMinutes(10), SizeBytes: 500_000); // modificado hace ~5 h
        var media = new[] { File(@"C:\rec\A\Muerto_1.mp4"), old };

        var plan = OrphanSegmentReconciler.Plan(known, started, media, now, TimeSpan.FromSeconds(60));

        var created = Assert.Single(plan.ToCreate);
        Assert.Equal(@"C:\rec\A\Muerto_2.mp4", created.FilePath);
        Assert.Equal(1, created.Index);
    }

    [Fact]
    public void MediaScanPatterns_CoverAllEngineContainers_NotOnlyMp4Mov()
    {
        // N27: antes solo se escaneaban *.mp4/*.mov; un huérfano en MKV/TS/MXF quedaba invisible. Los patrones
        // se derivan del catálogo de contenedores del motor, así que cubren los recomendados para largos.
        Assert.Contains("*.mkv", OrphanSegmentReconciler.MediaPatterns);
        Assert.Contains("*.ts", OrphanSegmentReconciler.MediaPatterns);
        Assert.Contains("*.mxf", OrphanSegmentReconciler.MediaPatterns);
        Assert.Contains("*.mp4", OrphanSegmentReconciler.MediaPatterns); // y siguen los originales
        Assert.Contains("*.mov", OrphanSegmentReconciler.MediaPatterns);
    }
}
