using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Scheduling;
using Xunit;

namespace Baioss.Record.UnitTests;

public class ScheduleEvaluatorTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(-6); // offset fijo: tests independientes de la zona local

    private static DateTimeOffset At(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, Off);

    // Zona de PRUEBA con offset fijo -6 (sin DST): así el evaluador produce el mismo instante que At(...) y los
    // tests son independientes de la zona de la máquina. Los métodos del evaluador toman ahora una TimeZoneInfo. (N4.)
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("fixed-minus6", Off, "fixed-6", "fixed-6");
    private static DateTimeOffset? Latest(ScheduledJob j, DateTimeOffset now) => ScheduleEvaluator.LatestSlotAtOrBefore(j, now, Tz);
    private static DateTimeOffset? Next(ScheduledJob j, DateTimeOffset now) => ScheduleEvaluator.NextSlotAfter(j, now, Tz);
    private static DateTimeOffset? OnDate(ScheduledJob j, DateOnly date, bool requireAfterAnchor = true) => ScheduleEvaluator.OccurrenceOnDate(j, date, Tz, requireAfterAnchor);

    private static ScheduledJob Job(RecurrenceKind kind, DateTimeOffset runAt, Weekdays days = Weekdays.None) => new()
    {
        ChannelId = Guid.NewGuid(),
        Action = ScheduledAction.StartRecording,
        RunAt = runAt,
        Recurrence = kind,
        Weekdays = days,
        Duration = TimeSpan.FromMinutes(60),
    };

    [Fact]
    public void Once_FiresOnlyAtItsInstant()
    {
        var job = Job(RecurrenceKind.Once, At(2026, 6, 25, 20, 0));

        Assert.Null(Latest(job, At(2026, 6, 25, 19, 0)));   // antes → nada
        Assert.Equal(At(2026, 6, 25, 20, 0), Latest(job, At(2026, 6, 25, 20, 0)));
        Assert.Equal(At(2026, 6, 25, 20, 0), Latest(job, At(2026, 6, 26, 10, 0))); // después → su franja

        Assert.Equal(At(2026, 6, 25, 20, 0), Next(job, At(2026, 6, 25, 19, 0))); // futura
        Assert.Null(Next(job, At(2026, 6, 26, 10, 0)));                          // ya pasó
    }

    [Fact]
    public void Daily_PicksMostRecentSlotAtOrBeforeNow()
    {
        var job = Job(RecurrenceKind.Daily, At(2026, 6, 20, 20, 0)); // ancla 20:00

        // 08:00 → la franja de hoy (20:00) aún no llegó → la de ayer.
        Assert.Equal(At(2026, 6, 20, 20, 0), Latest(job, At(2026, 6, 21, 8, 0)));
        // 21:00 → la franja de hoy ya pasó.
        Assert.Equal(At(2026, 6, 21, 20, 0), Latest(job, At(2026, 6, 21, 21, 0)));
    }

    [Fact]
    public void Daily_NextSlotRollsToTomorrowAfterTheSlot()
    {
        var job = Job(RecurrenceKind.Daily, At(2026, 6, 20, 20, 0));

        Assert.Equal(At(2026, 6, 21, 20, 0), Next(job, At(2026, 6, 21, 8, 0)));  // hoy
        Assert.Equal(At(2026, 6, 22, 20, 0), Next(job, At(2026, 6, 21, 21, 0))); // mañana
    }

    [Fact]
    public void Daily_BeforeAnchor_NoSlotButNextIsAnchor()
    {
        var job = Job(RecurrenceKind.Daily, At(2026, 6, 20, 20, 0));

        Assert.Null(Latest(job, At(2026, 6, 20, 10, 0)));            // antes del ancla
        Assert.Equal(At(2026, 6, 20, 20, 0), Next(job, At(2026, 6, 20, 10, 0))); // próxima = ancla
    }

    [Fact]
    public void Weekly_EveryDay_BehavesLikeDaily()
    {
        var job = Job(RecurrenceKind.Weekly, At(2026, 6, 20, 20, 0), Weekdays.EveryDay);
        Assert.Equal(At(2026, 6, 21, 20, 0), Latest(job, At(2026, 6, 21, 21, 0)));
    }

    [Fact]
    public void Weekly_SelectsOnlyChosenWeekdays()
    {
        var now = At(2026, 6, 24, 21, 0);
        var anchor = At(2026, 6, 1, 20, 0);

        // Día seleccionado = el de 'now' → la franja es hoy.
        var jobToday = Job(RecurrenceKind.Weekly, anchor, ScheduleEvaluator.ToFlag(now.DayOfWeek));
        Assert.Equal(At(2026, 6, 24, 20, 0), Latest(jobToday, now));

        // Día seleccionado = otro distinto al de 'now' → retrocede a la ocurrencia anterior de ESE día.
        var otherDay = now.AddDays(1).DayOfWeek;
        var jobOther = Job(RecurrenceKind.Weekly, anchor, ScheduleEvaluator.ToFlag(otherDay));
        var latest = Latest(jobOther, now);
        Assert.NotNull(latest);
        Assert.Equal(otherDay, latest!.Value.DayOfWeek);
        Assert.True(latest <= now);
    }

    [Fact]
    public void Weekly_NoDaysSelected_NeverFires()
    {
        var job = Job(RecurrenceKind.Weekly, At(2026, 6, 1, 20, 0), Weekdays.None);
        Assert.Null(Latest(job, At(2026, 6, 24, 21, 0)));
        Assert.Null(Next(job, At(2026, 6, 24, 21, 0)));
    }

    // --- OccurrenceOnDate (sección "HOY") ---

    private static DateOnly D(int y, int mo, int d) => new(y, mo, d);

    [Fact]
    public void OccurrenceOnDate_Once_OnlyOnItsOwnDate()
    {
        var job = Job(RecurrenceKind.Once, At(2026, 6, 25, 20, 0));
        Assert.Equal(At(2026, 6, 25, 20, 0), OnDate(job, D(2026, 6, 25)));
        Assert.Null(OnDate(job, D(2026, 6, 24)));
        Assert.Null(OnDate(job, D(2026, 6, 26)));
    }

    [Fact]
    public void OccurrenceOnDate_Daily_EveryDayFromTheAnchor()
    {
        var job = Job(RecurrenceKind.Daily, At(2026, 6, 20, 20, 0));
        Assert.Equal(At(2026, 6, 22, 20, 0), OnDate(job, D(2026, 6, 22)));
        Assert.Null(OnDate(job, D(2026, 6, 19))); // antes de la primera ocurrencia
    }

    [Fact]
    public void OccurrenceOnDate_Weekly_OnlyOnSelectedWeekday()
    {
        var date = D(2026, 6, 22);
        var job = Job(RecurrenceKind.Weekly, At(2026, 6, 1, 20, 0), ScheduleEvaluator.ToFlag(date.DayOfWeek));
        Assert.Equal(At(2026, 6, 22, 20, 0), OnDate(job, date));
        Assert.Null(OnDate(job, date.AddDays(1))); // otro día de la semana
    }

    [Fact]
    public void OccurrenceOnDate_IgnoringAnchor_ShowsTodaySlotEvenIfBeforeAnchor()
    {
        // Caso del usuario: semanal TODOS los días @18:00, creada un domingo 23:29 → el ancla rodó al lunes.
        // La vista de HOY (requireAfterAnchor:false) DEBE mostrar la franja de hoy (domingo 18:00); el
        // chequeo por defecto (con candado) la descarta por quedar antes del ancla.
        var job = Job(RecurrenceKind.Weekly, At(2026, 6, 22, 18, 0), Weekdays.EveryDay); // ancla lunes 22
        var sunday = D(2026, 6, 21);

        Assert.Null(OnDate(job, sunday));                    // con candado
        Assert.Equal(At(2026, 6, 21, 18, 0),
            OnDate(job, sunday, requireAfterAnchor: false)); // vista HOY
    }

    [Fact]
    public void Daily_FiresAtSameLocalTime_AcrossDstTransition()
    {
        // N4: una diaria a las 20:00 en una zona con DST debe seguir disparando a las 20:00 LOCALES tras el cambio
        // de horario de verano (antes, con el offset FIJO capturado al crear, se corría 1 h). Zona de prueba con
        // DST real (EE. UU. Este): spring-forward el 8-mar-2026.
        var tz = EasternTz();
        var anchorLocal = new DateTime(2026, 2, 1, 20, 0, 0, DateTimeKind.Unspecified); // 1-feb, aún en EST (-5)
        var anchor = new DateTimeOffset(anchorLocal, tz.GetUtcOffset(anchorLocal));
        var job = new ScheduledJob
        {
            ChannelId = Guid.NewGuid(), Action = ScheduledAction.StartRecording,
            RunAt = anchor, Recurrence = RecurrenceKind.Daily, Duration = TimeSpan.FromMinutes(60),
        };

        // 'now' = 15-mar 21:00 local (ya en EDT, -4, tras el spring-forward): la franja de hoy ya pasó.
        var nowLocal = new DateTime(2026, 3, 15, 21, 0, 0, DateTimeKind.Unspecified);
        var now = new DateTimeOffset(nowLocal, tz.GetUtcOffset(nowLocal));

        var slot = ScheduleEvaluator.LatestSlotAtOrBefore(job, now, tz);
        Assert.NotNull(slot);
        var slotLocal = TimeZoneInfo.ConvertTime(slot!.Value, tz);
        Assert.Equal(20, slotLocal.Hour);                       // sigue a las 20:00 locales (no 21:00 por el DST)
        Assert.Equal(new DateOnly(2026, 3, 15), DateOnly.FromDateTime(slotLocal.DateTime));
        Assert.Equal(TimeSpan.FromHours(-4), slot.Value.Offset); // y con el offset EDT vigente ese día
    }

    private static TimeZoneInfo EasternTz()
    {
        foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* siguiente */ }
        throw new InvalidOperationException("Zona horaria Eastern no disponible en esta máquina.");
    }
}
