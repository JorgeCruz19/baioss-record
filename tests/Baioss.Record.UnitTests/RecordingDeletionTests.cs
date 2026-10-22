using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Audit;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;
using Baioss.Record.Infrastructure;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.Infrastructure.Persistence;
using Baioss.Record.Infrastructure.Storage;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Borrado MANUAL de grabaciones (ventana «Borrar grabaciones»), sobre una BD SQLite y archivos reales en una carpeta
/// temporal: qué se borra (todas las piezas y sus filas), qué se queda solo en registro (el archivo ya no estaba) y qué
/// no se toca nunca (en curso, protegidas, con un archivo en uso o sin permiso).
/// </summary>
public sealed class RecordingDeletionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"baioss-del-{Guid.NewGuid():N}.db");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"baioss-del-{Guid.NewGuid():N}");
    private readonly ServiceProvider _sp;
    private readonly IRecordingSessionRepository _sessions;
    private readonly IRepository<Segment> _segments;
    private readonly List<RecordingDeleted> _published = new();
    private readonly HashSet<Guid> _recordingNow = new();

    public RecordingDeletionTests()
    {
        Directory.CreateDirectory(_dir);
        _sp = new ServiceCollection().AddLogging().AddBaiossInfrastructure(_dbPath).BuildServiceProvider();
        _sp.EnsureBaiossDatabaseCreated();
        _sessions = _sp.GetRequiredService<IRecordingSessionRepository>();
        _segments = _sp.GetRequiredService<IRepository<Segment>>();
        _sp.GetRequiredService<IEventBus>().Subscribe<RecordingDeleted>((e, _) => { lock (_published) _published.Add(e); return Task.CompletedTask; });
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" }) try { File.Delete(f); } catch { /* best effort */ }
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    /// <summary>El servicio con la lista de «lo que se graba ahora» de la prueba (el registrado en DI no tiene canales).</summary>
    private RecordingDeletionService Service() => new(_sessions, NullLogger<RecordingDeletionService>.Instance,
        _sp.GetRequiredService<IEventBus>(), () => _recordingNow);

    /// <summary>Una grabación terminada (o en curso, con <paramref name="ended"/> = false) con un archivo por pieza. Las
    /// piezas cuyo contenido es null quedan registradas pero sin archivo en el disco.</summary>
    private async Task<RecordingSession> AddRecordingAsync(string name, string?[] pieces, bool ended = true,
        RecordingProtection protection = RecordingProtection.None)
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var session = new RecordingSession
        {
            ChannelId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), InputSourceId = Guid.NewGuid(),
            StartedAt = start, EndedAt = ended ? start.AddMinutes(30) : null,
            State = ended ? RecordingState.Idle : RecordingState.Recording, Protection = protection,
        };
        await _sessions.AddAsync(session);
        for (int i = 0; i < pieces.Length; i++)
        {
            var path = Path.Combine(_dir, $"{name}_{i + 1}.mp4");
            if (pieces[i] is { } content) await File.WriteAllTextAsync(path, content);
            var seg = new Segment
            {
                SessionId = session.Id, Index = i, FilePath = path, Status = SegmentStatus.Completed,
                StartedAt = start, EndedAt = start.AddMinutes(10), SizeBytes = pieces[i]?.Length ?? 1234,
            };
            await _segments.AddAsync(seg);
            session.Segments.Add(seg);
        }
        return session;
    }

    private async Task<int> SegmentRowsAsync(Guid sessionId)
    {
        await using var db = await _sp.GetRequiredService<IDbContextFactory<BaiossDbContext>>().CreateDbContextAsync();
        return await db.Segments.CountAsync(s => s.SessionId == sessionId);
    }

    [Fact]
    public async Task Una_Grabacion_Partida_Se_Borra_Entera_Archivos_Y_Registros()
    {
        var rec = await AddRecordingAsync("Noticias", new[] { "aaaa", "bbbbbb", "cc" });

        var result = await Service().DeleteAsync(new[] { rec.Id }, "jorge");

        Assert.All(rec.Segments, s => Assert.False(File.Exists(s.FilePath), $"Quedó {s.FilePath}"));
        Assert.Null(await _sessions.GetWithSegmentsAsync(rec.Id));
        Assert.Equal(0, await SegmentRowsAsync(rec.Id)); // ninguna fila de segmento sin su sesión
        Assert.Equal(1, result.Recordings);
        Assert.Equal(3, result.FilesDeleted);
        Assert.Equal(12, result.BytesFreed);
        Assert.Equal(0, result.FilesMissing);
        Assert.Empty(result.Issues);

        var audit = Assert.Single(_published);
        Assert.Equal(rec.Id, audit.SessionId);
        Assert.Equal(3, audit.Files);
        Assert.Equal(12, audit.TotalBytes);
        Assert.Equal("Noticias_1.mp4", audit.FileName);
        Assert.Equal("jorge", audit.Operator);
    }

    [Fact]
    public async Task Si_El_Archivo_Ya_No_Esta_Solo_Se_Borra_El_Registro()
    {
        var mixed = await AddRecordingAsync("Mixta", new[] { "abc", null });   // la 2.ª pieza la borraron a mano
        var gone = await AddRecordingAsync("Fantasma", new string?[] { null });
        var empty = await AddRecordingAsync("SinArchivos", Array.Empty<string?>()); // nunca llegó a cerrar una pieza

        var result = await Service().DeleteAsync(new[] { mixed.Id, gone.Id, empty.Id }, "jorge");

        Assert.False(File.Exists(mixed.Segments[0].FilePath));
        foreach (var r in new[] { mixed, gone, empty }) Assert.Null(await _sessions.GetWithSegmentsAsync(r.Id));
        Assert.Equal(3, result.Recordings);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(2, result.FilesMissing);
    }

    [Fact]
    public async Task Una_Grabacion_En_Curso_No_Se_Toca()
    {
        var open = await AddRecordingAsync("Abierta", new[] { "x" }, ended: false);   // aún sin fin en la BD
        var live = await AddRecordingAsync("EnElCanal", new[] { "y" });               // con fin, pero un canal la escribe
        _recordingNow.Add(live.Id);

        var result = await Service().DeleteAsync(new[] { open.Id, live.Id }, "jorge");

        Assert.Equal(2, result.InProgress);
        Assert.Equal(0, result.Recordings);
        foreach (var r in new[] { open, live })
        {
            Assert.True(File.Exists(r.Segments[0].FilePath));
            Assert.NotNull(await _sessions.GetWithSegmentsAsync(r.Id));
        }
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Las_Protegidas_E_Importantes_No_Se_Borran()
    {
        var p = await AddRecordingAsync("Protegida", new[] { "x" }, protection: RecordingProtection.Protected);
        var i = await AddRecordingAsync("Importante", new[] { "y" }, protection: RecordingProtection.Important);

        var result = await Service().DeleteAsync(new[] { p.Id, i.Id }, "jorge");

        Assert.Equal(2, result.Protected);
        Assert.True(File.Exists(p.Segments[0].FilePath));
        Assert.True(File.Exists(i.Segments[0].FilePath));
        Assert.Equal(1, await SegmentRowsAsync(p.Id));
        Assert.Equal(1, await SegmentRowsAsync(i.Id));
    }

    [Fact]
    public async Task Con_Un_Archivo_En_Uso_No_Se_Borra_Nada_De_Esa_Grabacion()
    {
        var rec = await AddRecordingAsync("EnUso", new[] { "uno", "dos" });
        var other = await AddRecordingAsync("Libre", new[] { "tres" });

        RecordingDeletionResult result;
        // Abierto como lo abre un reproductor (compartiendo lectura): no concede la apertura exclusiva.
        using (File.Open(rec.Segments[1].FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await Service().DeleteAsync(new[] { rec.Id, other.Id }, "jorge");

        Assert.True(File.Exists(rec.Segments[0].FilePath), "La pieza libre de una grabación con otra en uso no se toca.");
        Assert.True(File.Exists(rec.Segments[1].FilePath));
        Assert.Equal(2, await SegmentRowsAsync(rec.Id));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("EnUso_2.mp4", issue.FileName);
        Assert.Equal(RecordingFileProblem.InUse, issue.Problem);

        Assert.False(File.Exists(other.Segments[0].FilePath)); // las demás se borran igual
        Assert.Equal(1, result.Recordings);
    }

    [Fact]
    public async Task Un_Archivo_De_Solo_Lectura_Se_Avisa_Y_Su_Grabacion_Se_Queda()
    {
        var rec = await AddRecordingAsync("SoloLectura", new[] { "x" });
        File.SetAttributes(rec.Segments[0].FilePath, FileAttributes.ReadOnly);

        var result = await Service().DeleteAsync(new[] { rec.Id }, "jorge");

        Assert.True(File.Exists(rec.Segments[0].FilePath));
        Assert.NotNull(await _sessions.GetWithSegmentsAsync(rec.Id));
        Assert.Equal(RecordingFileProblem.AccessDenied, Assert.Single(result.Issues).Problem);
    }

    [Fact]
    public async Task Una_Que_Ya_No_Existe_No_Cuenta_Ni_Falla()
    {
        var result = await Service().DeleteAsync(new[] { Guid.NewGuid() }, "jorge");

        Assert.Equal(0, result.Recordings);
        Assert.Empty(result.Issues);
        Assert.Equal(0, result.DatabaseErrors);
    }

    [Fact]
    public async Task El_Avance_Cuenta_Cada_Grabacion_Tratada()
    {
        var a = await AddRecordingAsync("A", new[] { "1" });
        var b = await AddRecordingAsync("B", new[] { "2" }, protection: RecordingProtection.Protected);
        var seen = new List<int>();

        await Service().DeleteAsync(new[] { a.Id, b.Id }, "jorge", new SyncProgress(seen.Add));

        Assert.Equal(new[] { 1, 2 }, seen);
    }

    [Fact]
    public async Task El_Plan_Cuenta_Archivos_Bytes_Ausentes_Y_Los_Que_No_Se_Tocan()
    {
        var a = await AddRecordingAsync("Plan", new[] { "12345", null, "123" });
        var p = await AddRecordingAsync("PlanProtegida", new[] { "x" }, protection: RecordingProtection.Protected);
        var open = await AddRecordingAsync("PlanAbierta", new[] { "y" }, ended: false);

        var plan = Service().Plan(new[] { a, p, open });

        Assert.Equal(1, plan.Recordings);
        Assert.Equal(2, plan.FilesFound);
        Assert.Equal(8, plan.BytesFound);
        Assert.Equal(1, plan.FilesMissing);
        Assert.Equal(1, plan.Protected);
        Assert.Equal(1, plan.InProgress);
        Assert.Empty(plan.UnavailableLocations);
        Assert.True(File.Exists(a.Segments[0].FilePath), "El plan no borra nada.");
    }

    [Fact]
    public async Task Un_Disco_Que_No_Responde_Se_Nombra_En_El_Plan_Y_Su_Registro_Se_Borra()
    {
        // Una letra de unidad que no existe en este equipo hace de disco desconectado.
        var letter = "QRSTUVWXYZJKLMNOP".Select(c => $"{c}:\\").FirstOrDefault(r => !Directory.Exists(r));
        Assert.NotNull(letter);
        var rec = await AddRecordingAsync("Desconectado", Array.Empty<string?>());
        var seg = new Segment { SessionId = rec.Id, Index = 0, FilePath = Path.Combine(letter!, "rec", "A_1.mp4"), SizeBytes = 10 };
        await _segments.AddAsync(seg);
        rec.Segments.Add(seg);

        var plan = Service().Plan(new[] { rec });
        Assert.Equal(letter, Assert.Single(plan.UnavailableLocations));
        Assert.Equal(1, plan.FilesMissing);

        // Lo que pidió el usuario: si el archivo no se encuentra, se borra solo el registro (la confirmación ya lo avisó).
        var result = await Service().DeleteAsync(new[] { rec.Id }, "jorge");
        Assert.Equal(1, result.Recordings);
        Assert.Equal(1, result.FilesMissing);
        Assert.Null(await _sessions.GetWithSegmentsAsync(rec.Id));
    }

    [Fact]
    public async Task Sin_Canales_Registrados_Solo_Cuenta_El_Fin_En_La_BD()
    {
        // El que compone la aplicación (DI): sin gestor de canales, «lo que se graba ahora» está vacío y no falla.
        var service = _sp.GetRequiredService<IRecordingDeletionService>();
        Assert.Empty(service.RecordingNow());

        var open = await AddRecordingAsync("DI", new[] { "x" }, ended: false);
        Assert.Equal(1, (await service.DeleteAsync(new[] { open.Id }, "jorge")).InProgress);
    }

    [Fact]
    public async Task El_Historial_Y_El_Registro_Aceptan_Limites_En_Hora_Local()
    {
        // La ventana pide «el 19/10» en hora de Guatemala (−06:00) y las fechas se guardan en UTC. La columna es el número del
        // conversor de EF (hora del reloj de la fecha + su desfase): sin pasar los límites a UTC, la grabación de las 22:51
        // del 19 (04:51 UTC del 20) quedaba fuera.
        var gt = TimeSpan.FromHours(-6);
        var from = new DateTimeOffset(2026, 10, 19, 0, 0, 0, gt);
        var to = new DateTimeOffset(2026, 10, 19, 23, 59, 59, gt);
        async Task<Guid> At(DateTimeOffset utc)
        {
            var s = new RecordingSession { ChannelId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), InputSourceId = Guid.NewGuid(), StartedAt = utc, EndedAt = utc.AddMinutes(1) };
            await _sessions.AddAsync(s);
            return s.Id;
        }
        var night = await At(new DateTimeOffset(2026, 10, 20, 4, 51, 0, TimeSpan.Zero));   // 22:51 del 19 en Guatemala
        await At(new DateTimeOffset(2026, 10, 20, 6, 30, 0, TimeSpan.Zero));               // 00:30 del 20: fuera
        await At(new DateTimeOffset(2026, 10, 19, 5, 0, 0, TimeSpan.Zero));                // 23:00 del 18: fuera

        var list = await _sessions.GetHistoryAsync(null, from, to, 0, 100);
        Assert.Equal(night, Assert.Single(list).Id);

        var events = _sp.GetRequiredService<IEventLogRepository>();
        await events.AppendAsync(new EventLogEntry { Category = "Dentro", Message = "x", Timestamp = new DateTimeOffset(2026, 10, 20, 4, 51, 0, TimeSpan.Zero) });
        await events.AppendAsync(new EventLogEntry { Category = "Fuera", Message = "y", Timestamp = new DateTimeOffset(2026, 10, 20, 6, 30, 0, TimeSpan.Zero) });
        Assert.Equal("Dentro", Assert.Single(await events.QueryAsync(null, from, to, 100)).Category);
    }

    [Fact]
    public async Task El_Repositorio_Conserva_La_Sesion_Mientras_Le_Quede_Algun_Segmento()
    {
        var rec = await AddRecordingAsync("Repo", new[] { "a", "b" });

        Assert.False(await _sessions.RemoveRecordsAsync(rec.Id, new[] { rec.Segments[0].Id }));
        var left = await _sessions.GetWithSegmentsAsync(rec.Id);
        Assert.NotNull(left);
        Assert.Equal(rec.Segments[1].Id, Assert.Single(left!.Segments).Id);

        Assert.True(await _sessions.RemoveRecordsAsync(rec.Id, new[] { rec.Segments[1].Id }));
        Assert.Null(await _sessions.GetWithSegmentsAsync(rec.Id));
        Assert.Equal(0, await SegmentRowsAsync(rec.Id));
    }

    [Fact]
    public async Task El_Registro_De_Actividad_Borra_Solo_Las_Entradas_Pedidas_Aunque_Sean_Muchas()
    {
        var events = _sp.GetRequiredService<IEventLogRepository>();
        var factory = _sp.GetRequiredService<IDbContextFactory<BaiossDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            for (int i = 0; i < 1_200; i++)
                db.EventLog.Add(new EventLogEntry { Category = "Prueba", Message = $"e{i}", Timestamp = DateTimeOffset.UtcNow.AddSeconds(-i) });
            await db.SaveChangesAsync();
        }
        List<long> ids;
        await using (var db = await factory.CreateDbContextAsync())
            ids = await db.EventLog.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();

        // Más que un lote (500) para que se borre en varios, y una que no existe (no cuenta).
        var doomed = ids.Take(1_100).Append(long.MaxValue).ToList();
        int removed = await events.DeleteAsync(doomed);

        Assert.True(doomed.Count > 2 * EventLogRepository.DeleteBatchSize);
        Assert.Equal(1_100, removed);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(ids.Skip(1_100), await db.EventLog.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync());
    }

    /// <summary>Avance que se apunta en el acto (Progress&lt;T&gt; lo mandaría al pool, sin orden).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}

/// <summary>Cómo se leen en «Actividad» los dos sucesos nuevos: una grabación borrada y entradas del registro borradas.</summary>
[Collection("Localizer")] // el idioma es estado global: estas clases no se ejecutan en paralelo entre sí
public sealed class DeletionAuditTextTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;

    public DeletionAuditTextTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    private static string Detail(IDomainEvent e)
    {
        var entry = EventLogWriter.ToEntry(e); // la misma copia en JSON que guarda el registro
        return AuditText.Detail(entry.Category, entry.PayloadJson, entry.Message);
    }

    [Fact]
    public void Una_Grabacion_Borrada_Se_Reconoce_Por_Su_Nombre_Fecha_Y_Lo_Que_Se_Borro()
    {
        var start = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);
        var e = new RecordingDeleted(Guid.NewGuid(), Guid.NewGuid(), "Noticias_1.mp4", start, 3, 2_254_857_830, 1, "jorge");
        var when = start.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        var detail = Detail(e);

        Assert.Equal("Grabación borrada", AuditText.Category(nameof(RecordingDeleted)));
        Assert.StartsWith($"«Noticias_1.mp4» del {when}", detail);
        Assert.Contains("3 archivos", detail);
        Assert.Contains("2.1 GB", detail.Replace(',', '.'));
        Assert.Contains("1 ya no estaba en el disco", detail);
        Assert.Equal("jorge", EventLogWriter.ToEntry(e).Operator); // quién: en su columna
    }

    [Fact]
    public void Una_Grabacion_Sin_Archivos_Dice_Que_Solo_Se_Quito_El_Registro()
    {
        var e = new RecordingDeleted(Guid.NewGuid(), Guid.NewGuid(), "A_20261021_200000.mp4", DateTimeOffset.UtcNow, 0, 0, 2, "jorge");

        Assert.Contains("solo se quitó el registro", Detail(e));
    }

    [Fact]
    public void Las_Entradas_Borradas_Dicen_Cuantas_Y_De_Que_Fechas()
    {
        var oldest = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var newest = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var d1 = oldest.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var d2 = newest.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        Assert.Equal("Actividad borrada", AuditText.Category(nameof(AuditEntriesDeleted)));
        Assert.Equal($"25 entradas · del {d1} al {d2}", Detail(new AuditEntriesDeleted(25, oldest, newest, "jorge")));
        Assert.Equal($"1 entrada · del {d2}", Detail(new AuditEntriesDeleted(1, newest, newest, "jorge")));

        Localizer.Language = AppLanguage.English;
        Assert.Equal($"25 entries · from {d1} to {d2}", Detail(new AuditEntriesDeleted(25, oldest, newest, "jorge")));
    }
}
