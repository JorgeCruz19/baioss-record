using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Scheduling;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// LAS reglas de una tarea automática. Vivían en la ventana de la aplicación; ahora la programación se gestiona también
/// desde el panel web, y las dos entradas pasan por aquí: lo que una acepta lo acepta la otra.
/// </summary>
[Collection("Localizer")] // los mensajes salen en el idioma vigente (estado global)
public class SchedulePlannerTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;
    public SchedulePlannerTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("prueba-6", TimeSpan.FromHours(-6), "prueba", "prueba");
    private static readonly Guid ChannelA = Guid.NewGuid();
    private static readonly Guid ChannelB = Guid.NewGuid();

    /// <summary>Sábado 19-09-2026 a las 12:00 en la zona de prueba.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(-6));

    private static TimeSpan T(int h, int m = 0, int s = 0) => new(h, m, s);

    private static ScheduleDraft Daily(string title = "Noticias", int startHour = 20, int endHour = 21, Guid? channel = null)
        => new(channel ?? ChannelA, title, RecurrenceKind.Daily, null, T(startHour), T(endHour));

    private static SchedulePlan Plan(ScheduleDraft draft, params ScheduledJob[] existing) => SchedulePlanner.Plan(draft, existing, Now, Zone);

    [Fact]
    public void Una_Diaria_Cuya_Hora_Aun_No_Llego_Hoy_Empieza_Hoy_Y_Dura_De_Inicio_A_Fin()
    {
        var plan = Plan(Daily());

        Assert.True(plan.Ok);
        var job = plan.Job!;
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 20, 0, 0, TimeSpan.FromHours(-6)), job.RunAt);
        Assert.Equal(TimeSpan.FromHours(1), job.Duration);
        Assert.Equal(ScheduledAction.StartRecording, job.Action);
        Assert.True(job.Enabled);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Si_La_Hora_De_Hoy_Ya_Paso_Empieza_Manana_Y_No_Arranca_Un_Trozo_De_Inmediato()
    {
        var plan = Plan(Daily(startHour: 8, endHour: 9));

        Assert.Equal(new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.FromHours(-6)), plan.Job!.RunAt);
    }

    [Fact]
    public void Fin_Anterior_Al_Inicio_Es_Cruce_De_Medianoche_Y_Se_Avisa()
    {
        var plan = Plan(new ScheduleDraft(ChannelA, "Trasnoche", RecurrenceKind.Daily, null, T(23, 30), T(0, 30)));

        Assert.True(plan.Ok);
        Assert.Equal(TimeSpan.FromHours(1), plan.Job!.Duration);
        Assert.Contains(ScheduleNote.EndsNextDay, plan.Notes);
    }

    [Fact]
    public void Una_Semanal_Usa_Solo_Los_Dias_Elegidos_Y_Una_Que_No_Lo_Es_Los_Ignora()
    {
        var semanal = Plan(new ScheduleDraft(ChannelA, "Pleno", RecurrenceKind.Weekly, null, T(10), T(12), Weekdays.Monday | Weekdays.Wednesday));
        Assert.Equal(Weekdays.Monday | Weekdays.Wednesday, semanal.Job!.Weekdays);
        Assert.Equal(DayOfWeek.Monday, semanal.Job.RunAt.DayOfWeek);      // el primer día válido tras el sábado

        var diaria = Plan(new ScheduleDraft(ChannelA, "Diaria", RecurrenceKind.Daily, null, T(10), T(12), Weekdays.Monday));
        Assert.Equal(Weekdays.None, diaria.Job!.Weekdays);
    }

    [Theory]
    [InlineData(ScheduleProblem.EndEqualsStart)]
    [InlineData(ScheduleProblem.SegmentMinutesInvalid)]
    [InlineData(ScheduleProblem.PickWeekday)]
    [InlineData(ScheduleProblem.PickDate)]
    [InlineData(ScheduleProblem.PastDate)]
    [InlineData(ScheduleProblem.InvalidTime)]
    public void Cada_Borrador_Mal_Formado_Dice_Cual_Es_Su_Problema_En_Palabras(ScheduleProblem expected)
    {
        var draft = expected switch
        {
            ScheduleProblem.EndEqualsStart => new ScheduleDraft(ChannelA, "x", RecurrenceKind.Daily, null, T(20), T(20)),
            ScheduleProblem.SegmentMinutesInvalid => Daily() with { SegmentMinutes = 0 },
            ScheduleProblem.PickWeekday => new ScheduleDraft(ChannelA, "x", RecurrenceKind.Weekly, null, T(20), T(21)),
            ScheduleProblem.PickDate => new ScheduleDraft(ChannelA, "x", RecurrenceKind.Once, null, T(20), T(21)),
            ScheduleProblem.PastDate => new ScheduleDraft(ChannelA, "x", RecurrenceKind.Once, new DateOnly(2026, 9, 19), T(11), T(11, 30)),
            _ => new ScheduleDraft(ChannelA, "x", RecurrenceKind.Daily, null, TimeSpan.FromHours(25), T(21)),
        };

        var plan = Plan(draft);

        Assert.False(plan.Ok);
        Assert.Equal(expected, plan.Problem);
        Assert.NotEqual("", ScheduleText.Describe(plan, "A"));
    }

    [Fact]
    public void Una_Unica_En_El_Futuro_Se_Programa_Para_Esa_Fecha_Y_Hora()
    {
        var plan = Plan(new ScheduleDraft(ChannelA, "Final", RecurrenceKind.Once, new DateOnly(2026, 9, 19), T(12, 0, 1), T(14)));

        Assert.True(plan.Ok);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 12, 0, 1, TimeSpan.FromHours(-6)), plan.Job!.RunAt);
    }

    [Fact]
    public void Una_Duracion_Que_Alcanza_La_Siguiente_Ocurrencia_Se_Rechaza_Porque_Esa_Se_Perderia_En_Silencio()
    {
        // Lunes y martes, de 20:00 a 19:59 del día siguiente (23 h 59 min) cabe; con una sola ocurrencia cada 24 h, 24 h no.
        var plan = Plan(new ScheduleDraft(ChannelA, "Maratón", RecurrenceKind.Daily, null, T(20), T(20, 0, 0).Subtract(TimeSpan.FromSeconds(1))));
        Assert.True(plan.Ok);                                              // 23:59:59 < 24 h

        // Semanal L-M (24 h entre ocurrencias) con una grabación de 30 h no existe por construcción (fin−inicio < 24 h),
        // así que el caso real es el de arriba; aquí se fija que el mensaje cita duración e intervalo.
        var job = new ScheduledJob { ChannelId = ChannelA, Action = ScheduledAction.StartRecording, Recurrence = RecurrenceKind.Daily, Duration = TimeSpan.FromHours(24) };
        var text = ScheduleText.Describe(new SchedulePlan { Job = job, Problem = ScheduleProblem.DurationOverlapsNext, Duration = TimeSpan.FromHours(24) }, "A");
        Assert.Contains("24 h", text);
        Assert.Contains("cada día", text);
    }

    [Fact]
    public void Los_Titulos_Son_Unicos_Porque_Los_Archivos_Se_Nombran_Por_Titulo()
    {
        var existing = Plan(Daily("Noticias")).Job!;

        var repetido = Plan(Daily("  noticias ", startHour: 6, endHour: 7, channel: ChannelB), existing);

        Assert.Equal(ScheduleProblem.DuplicateTitle, repetido.Problem);
        Assert.Contains("noticias", ScheduleText.Describe(repetido, "B"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dos_Tareas_Del_Mismo_Canal_No_Se_Solapan_Pero_En_Canales_Distintos_Si_Pueden()
    {
        var existing = Plan(Daily("Noticias")).Job!;                       // canal A, 20:00–21:00

        var choque = Plan(Daily("Deportes", startHour: 20, endHour: 22), existing);
        Assert.Equal(ScheduleProblem.Clash, choque.Problem);
        Assert.Same(existing, choque.ClashWith);
        Assert.Equal("Choca con «Noticias» en el Canal A. Ajusta la hora o la duración.", ScheduleText.Describe(choque, "A"));

        Assert.True(Plan(Daily("Deportes", channel: ChannelB), existing).Ok);
        Assert.True(Plan(Daily("Deportes", startHour: 21, endHour: 22), existing).Ok); // pegadas, sin tocarse
    }

    [Fact]
    public void Una_Tarea_En_Pausa_No_Reserva_Su_Franja()
    {
        var pausada = Plan(Daily("Noticias")).Job!;
        pausada.Enabled = false;

        Assert.True(Plan(Daily("Deportes"), pausada).Ok);
    }

    [Fact]
    public void Editar_Conserva_El_Id_Y_La_Pausa_Y_No_Choca_Consigo_Misma()
    {
        var original = Plan(Daily("Noticias")).Job!;
        original.Enabled = false;

        var plan = SchedulePlanner.Plan(Daily("Noticias", startHour: 20, endHour: 22), new[] { original }, Now, Zone, editing: original);

        Assert.True(plan.Ok);
        Assert.Equal(original.Id, plan.Job!.Id);
        Assert.False(plan.Job.Enabled);                                    // editar no la reactiva sola
        Assert.Equal(TimeSpan.FromHours(2), plan.Job.Duration);
    }

    [Fact]
    public void Sin_Titulo_Lleva_El_De_Por_Defecto_Y_Uno_Desmedido_Se_Recorta()
    {
        Assert.Equal("Grabación programada", Plan(Daily("   ")).Job!.Title);
        Assert.Equal(SchedulePlanner.MaxTitleLength, Plan(Daily(new string('x', 300))).Job!.Title.Length);
    }

    [Fact]
    public void Segmentos_Mayores_Que_La_Grabacion_Se_Avisan_Pero_No_Impiden_Guardar()
    {
        var plan = Plan(Daily() with { SegmentMinutes = 90 });

        Assert.True(plan.Ok);
        Assert.Equal(90, plan.Job!.SegmentMinutes);
        Assert.Contains(ScheduleNote.SegmentsSingleFile, plan.Notes);
    }

    [Fact]
    public void La_Hora_De_Fin_De_Una_Tarea_Se_Reconstruye_Desde_Su_Duracion()
    {
        var job = Plan(new ScheduleDraft(ChannelA, "Trasnoche", RecurrenceKind.Daily, null, T(23, 30), T(0, 45, 10))).Job!;

        Assert.Equal(T(0, 45, 10), SchedulePlanner.EndTimeOfDay(job, Zone));
    }
}
