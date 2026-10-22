using System.Globalization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Storage;
using Baioss.Record.App.Audit;
using Baioss.Record.App.Storage;
using Xunit;

namespace Baioss.Record.App.Tests;

/// <summary>
/// Ventana «Borrar grabaciones»: qué se lista, cómo se traduce el rango de fechas, qué se puede marcar, qué se pregunta
/// antes de borrar y qué se le pide después al servicio. El borrado en sí (disco + BD) lo prueban los unitarios del servicio.
/// </summary>
[Collection("Localizer")] // el idioma es estado global: estas clases no se ejecutan en paralelo entre sí
public sealed class RecordingCleanupViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid ChannelA = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, string> Keys = new Dictionary<Guid, string> { [ChannelA] = "A" };

    private readonly AppLanguage _original = Localizer.Language;
    private readonly FakeSessions _sessions = new();
    private readonly FakeDeletion _deletion;
    private readonly FakeConfirm _confirm = new(answer: true);

    public RecordingCleanupViewModelTests()
    {
        Localizer.Language = AppLanguage.Spanish;
        _deletion = new FakeDeletion(_sessions);
    }

    public void Dispose() => Localizer.Language = _original;

    private RecordingSession Add(string name, DateTimeOffset start, int pieces = 1, bool ended = true,
        RecordingProtection protection = RecordingProtection.None)
    {
        var s = new RecordingSession
        {
            ChannelId = ChannelA, ProfileId = Guid.NewGuid(), InputSourceId = Guid.NewGuid(),
            StartedAt = start, EndedAt = ended ? start.AddMinutes(30) : null, Protection = protection,
        };
        for (int i = 0; i < pieces; i++)
            s.Segments.Add(new Segment { SessionId = s.Id, Index = i, FilePath = $@"D:\rec\{name}_{i + 1}.mp4", SizeBytes = 1_000_000 });
        _sessions.All.Add(s);
        return s;
    }

    private async Task<RecordingCleanupViewModel> OpenAsync()
    {
        var vm = new RecordingCleanupViewModel(_sessions, _deletion, new FixedClock(Now), Keys, _confirm.Ask);
        await vm.Loading;
        return vm;
    }

    [Fact]
    public async Task Lista_Todas_Por_Fecha_Y_No_Ofrece_Borrar_Las_En_Curso_Ni_Las_Protegidas()
    {
        var old = Add("Vieja", Now.AddDays(-3));
        var open = Add("Abierta", Now.AddMinutes(-5), ended: false);
        var live = Add("EnElCanal", Now.AddHours(-1));
        _deletion.Live.Add(live.Id); // terminada en la BD, pero un canal la está escribiendo
        var prot = Add("Protegida", Now.AddDays(-1), protection: RecordingProtection.Protected);

        var vm = await OpenAsync();

        Assert.Equal(new[] { open.Id, live.Id, prot.Id, old.Id }, vm.Rows.Select(r => r.Id)); // la más reciente primero
        Assert.True(vm.Rows[0].IsInProgress);
        Assert.True(vm.Rows[1].IsInProgress);
        Assert.True(vm.Rows[2].IsProtected);
        Assert.Equal(new[] { false, false, false, true }, vm.Rows.Select(r => r.CanDelete));
        Assert.Equal(1, vm.DeletableCount);
        Assert.Equal("● En curso", vm.Rows[0].StateText);
        Assert.StartsWith("4 grabaciones", vm.Summary);
    }

    [Fact]
    public async Task El_Rango_Va_Del_Primer_Instante_Del_Desde_Al_Ultimo_Del_Hasta_En_Hora_Local()
    {
        var vm = await OpenAsync();

        vm.FromDate = new DateTime(2026, 10, 1);
        vm.ToDate = new DateTime(2026, 10, 15);
        await vm.Loading;

        var (from, to) = _sessions.LastRange!.Value;
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 1)), from);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 16)).AddTicks(-1), to);

        // Al revés se entiende el mismo rango, y «Todas las fechas» lo quita.
        vm.FromDate = new DateTime(2026, 10, 15);
        vm.ToDate = new DateTime(2026, 10, 1);
        await vm.Loading;
        Assert.Equal((from, to), _sessions.LastRange!.Value);

        await vm.AllDatesCommand.ExecuteAsync(null);
        Assert.Null(vm.FromDate);
        Assert.Null(vm.ToDate);
        Assert.True(_sessions.LastRange!.Value.To > Now);
    }

    [Fact]
    public async Task El_Filtro_Deja_Fuera_Las_Grabaciones_De_Otros_Dias()
    {
        var inside = Add("Dentro", new DateTimeOffset(new DateTime(2026, 10, 10, 12, 0, 0)));
        Add("Antes", new DateTimeOffset(new DateTime(2026, 9, 30, 23, 59, 0)));
        Add("Despues", new DateTimeOffset(new DateTime(2026, 10, 16, 0, 1, 0)));
        var vm = await OpenAsync();

        vm.FromDate = new DateTime(2026, 10, 1);
        vm.ToDate = new DateTime(2026, 10, 15);
        await vm.Loading;

        Assert.Equal(inside.Id, Assert.Single(vm.Rows).Id);
    }

    [Fact]
    public async Task Si_No_Se_Confirma_No_Se_Borra_Nada()
    {
        Add("Uno", Now.AddHours(-1), pieces: 3);
        var vm = await OpenAsync();
        _confirm.Answer = false;

        await vm.DeleteOneCommand.ExecuteAsync(vm.Rows[0]);

        Assert.Single(_confirm.Asked);
        Assert.Empty(_deletion.DeleteCalls);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Borrar_Una_Pregunta_Con_Su_Nombre_Sus_Archivos_Y_Su_Tamano_Y_Luego_La_Quita_De_La_Lista()
    {
        var rec = Add("Noticias", Now.AddHours(-1), pieces: 3);
        var other = Add("Otra", Now.AddHours(-2));
        var vm = await OpenAsync();

        await vm.DeleteOneCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == rec.Id));

        var (text, title) = Assert.Single(_confirm.Asked);
        Assert.Equal("Borrar grabaciones", title);
        Assert.Contains("¿Borrar la grabación «Noticias_1.mp4» del canal A", text);
        Assert.Contains("3 archivos (3 MB)", text);   // todas las piezas cuentan
        Assert.Contains("No se puede deshacer", text);
        Assert.Equal(new[] { rec.Id }, Assert.Single(_deletion.DeleteCalls));
        Assert.Equal(new[] { other.Id }, vm.Rows.Select(r => r.Id)); // la lista se recargó
        Assert.Contains("Borrada 1 grabación", vm.StatusMessage);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task Marcar_Todas_Solo_Marca_Las_Que_Se_Pueden_Borrar()
    {
        var a = Add("A1", Now.AddHours(-1));
        var b = Add("B1", Now.AddHours(-2));
        var important = Add("I1", Now.AddHours(-3), protection: RecordingProtection.Important);
        Add("L1", Now.AddMinutes(-1), ended: false);
        var vm = await OpenAsync();

        vm.AllSelected = true;
        Assert.Equal(2, vm.SelectedCount);
        Assert.False(vm.Rows.Single(r => r.Id == important.Id).IsSelected);
        Assert.Equal("🗑 Borrar seleccionadas (2)", vm.DeleteSelectedText);

        vm.Rows.Single(r => r.Id == b.Id).IsSelected = false;
        Assert.Equal(1, vm.SelectedCount);
        Assert.Null(vm.AllSelected); // algunas sí y otras no

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { a.Id }, Assert.Single(_deletion.DeleteCalls));
        Assert.Contains("¿Borrar la grabación «A1_1.mp4»", _confirm.Asked[0].Text);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task Borrar_El_Rango_Borra_Todo_Lo_Listado_Salvo_En_Curso_Y_Protegidas_Y_Lo_Dice()
    {
        var a = Add("A1", Now.AddDays(-1));
        var b = Add("B1", Now.AddDays(-2));
        Add("P1", Now.AddDays(-3), protection: RecordingProtection.Protected);
        Add("L1", Now.AddMinutes(-1), ended: false);
        Add("Fuera", Now.AddDays(-40)); // fuera del rango: ni se lista ni se borra
        var vm = await OpenAsync();
        var since = Now.ToLocalTime().Date.AddDays(-7);
        vm.FromDate = since;
        await vm.Loading;

        Assert.Equal("🗑 Borrar todo el rango (2)", vm.DeleteRangeText);
        await vm.DeleteRangeCommand.ExecuteAsync(null);

        var text = Assert.Single(_confirm.Asked).Text;
        Assert.Contains($"¿Borrar las 2 grabaciones de la lista (desde el {since.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)})?", text);
        Assert.Contains("No se borrarán: 1 en curso y 1 protegida o importante.", text);
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), Assert.Single(_deletion.DeleteCalls).OrderBy(x => x));
        Assert.Equal(2, vm.Rows.Count); // quedan la protegida y la que está en curso
    }

    [Fact]
    public async Task Sin_Nada_Borrable_Los_Botones_No_Se_Activan()
    {
        Add("P1", Now.AddDays(-1), protection: RecordingProtection.Protected);
        Add("L1", Now.AddMinutes(-1), ended: false);
        var vm = await OpenAsync();

        Assert.False(vm.DeleteRangeCommand.CanExecute(null));
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
        Assert.All(vm.Rows, r => Assert.False(vm.DeleteOneCommand.CanExecute(r)));
    }

    [Fact]
    public async Task Un_Archivo_En_Uso_Se_Avisa_En_Rojo_Y_Su_Grabacion_Sigue_En_La_Lista()
    {
        Add("Bloqueada", Now.AddHours(-1));
        _deletion.IssuesToReport.Add(new RecordingFileIssue("Bloqueada_1.mp4", RecordingFileProblem.InUse));
        var vm = await OpenAsync();

        await vm.DeleteOneCommand.ExecuteAsync(vm.Rows[0]);

        Assert.True(vm.StatusIsError);
        Assert.Contains("«Bloqueada_1.mp4» está en uso por otro programa", vm.StatusMessage);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Los_Textos_Siguen_El_Idioma()
    {
        Localizer.Language = AppLanguage.English;
        Add("Uno", Now.AddHours(-1));
        var vm = await OpenAsync();

        Assert.Equal("🗑 Delete selected (0)", vm.DeleteSelectedText);
        Assert.StartsWith("1 recording", vm.Summary);
        await vm.DeleteOneCommand.ExecuteAsync(vm.Rows[0]);
        Assert.StartsWith("Delete the recording «Uno_1.mp4» from channel A", Assert.Single(_confirm.Asked).Text);
    }
}

/// <summary>Ventana «Actividad»: borrar entradas marcadas, con confirmación y dejando constancia.</summary>
[Collection("Localizer")]
public sealed class AuditViewModelDeletionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<Guid, string> Keys = new Dictionary<Guid, string>();

    private readonly AppLanguage _original = Localizer.Language;
    private readonly FakeEvents _events = new();
    private readonly FakeBus _bus = new();
    private readonly FakeConfirm _confirm = new(answer: true);

    public AuditViewModelDeletionTests()
    {
        Localizer.Language = AppLanguage.Spanish;
        _events.All.Add(new EventLogEntry { Id = 1, Category = "RecordingStarted", Message = "a", Timestamp = Now.AddDays(-3) });
        _events.All.Add(new EventLogEntry { Id = 2, Category = "SignalLost", Message = "b", Timestamp = Now.AddDays(-2), Severity = EventSeverity.Warning });
        _events.All.Add(new EventLogEntry { Id = 3, Category = "RecordingDeleted", Message = "c", Timestamp = Now.AddDays(-1) });
    }

    public void Dispose() => Localizer.Language = _original;

    private async Task<AuditViewModel> OpenAsync()
    {
        var vm = new AuditViewModel(_events, new FixedClock(Now), Keys, _bus, _confirm.Ask) { TraceSettleDelay = TimeSpan.Zero };
        await vm.Loading;
        return vm;
    }

    [Fact]
    public async Task Borra_Las_Marcadas_Tras_Confirmar_Y_Deja_Constancia()
    {
        var vm = await OpenAsync();
        Assert.Equal(new long[] { 3, 2, 1 }, vm.Entries.Select(e => e.Id)); // la más reciente primero
        vm.Entries.Single(e => e.Id == 3).IsSelected = true;
        vm.Entries.Single(e => e.Id == 1).IsSelected = true;
        Assert.Equal(2, vm.SelectedCount);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        var (text, title) = Assert.Single(_confirm.Asked);
        Assert.Equal("Borrar actividad", title);
        Assert.StartsWith("¿Borrar 2 entradas del registro de actividad?", text);
        Assert.Contains("«Actividad borrada»", text);
        Assert.Equal(new long[] { 1, 3 }, _events.Deleted.Order());

        var trace = Assert.IsType<AuditEntriesDeleted>(Assert.Single(_bus.Published));
        Assert.Equal(2, trace.Count);
        Assert.Equal(Now.AddDays(-3), trace.Oldest);
        Assert.Equal(Now.AddDays(-1), trace.Newest);
        Assert.Equal(Environment.UserName, trace.Operator);

        Assert.Equal(2, Assert.Single(vm.Entries).Id); // recargada
        Assert.Equal("✔ Borradas 2 entradas del registro.", vm.StatusMessage);
    }

    [Fact]
    public async Task Si_No_Se_Confirma_No_Se_Borra_Ni_Se_Anota_Nada()
    {
        var vm = await OpenAsync();
        vm.Entries[0].IsSelected = true;
        _confirm.Answer = false;

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(_events.Deleted);
        Assert.Empty(_bus.Published);
        Assert.Equal(3, vm.Entries.Count);
    }

    [Fact]
    public async Task Marcar_Todas_Y_Desmarcar_Una_Deja_La_Casilla_A_Medias()
    {
        var vm = await OpenAsync();
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));

        vm.AllSelected = true;
        Assert.Equal(3, vm.SelectedCount);
        Assert.Equal("🗑 Borrar seleccionadas (3)", vm.DeleteSelectedText);
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        vm.Entries[1].IsSelected = false;
        Assert.Null(vm.AllSelected);
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public async Task Una_Grabacion_Borrada_Cuenta_Como_Suceso_De_Grabacion()
    {
        var vm = await OpenAsync();

        vm.SelectedKind = vm.Kinds.Single(k => k.Kind == AuditKind.Recordings);
        await vm.Loading;

        Assert.Equal(new long[] { 3, 1 }, vm.Entries.Select(e => e.Id));
        Assert.Equal("Grabación borrada", vm.Entries[0].EventText);
    }
}
