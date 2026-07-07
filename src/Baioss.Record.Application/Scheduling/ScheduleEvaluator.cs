using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Scheduling;

/// <summary>
/// Cálculo PURO (sin estado ni reloj) de las ocurrencias de un <see cref="ScheduledJob"/>: la franja
/// vigente (para disparar) y la próxima (para mostrar). La "hora del día" y el "día de la semana" se
/// evalúan en una ZONA HORARIA (el parámetro <c>tz</c>; por defecto la local): así una diaria de las 20:00
/// dispara a las 20:00 LOCALES todo el año, aunque cambie el horario de verano. Antes se usaba el offset
/// FIJO capturado al crear el trabajo, lo que corría las franjas 1 h tras cada transición DST. (Auditoría N4.)
/// </summary>
public static class ScheduleEvaluator
{
    public static Weekdays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Weekdays.Monday,
        DayOfWeek.Tuesday => Weekdays.Tuesday,
        DayOfWeek.Wednesday => Weekdays.Wednesday,
        DayOfWeek.Thursday => Weekdays.Thursday,
        DayOfWeek.Friday => Weekdays.Friday,
        DayOfWeek.Saturday => Weekdays.Saturday,
        _ => Weekdays.Sunday,
    };

    public static bool Includes(Weekdays mask, DayOfWeek day) => (mask & ToFlag(day)) != 0;

    /// <summary>La franja más reciente que empieza en o antes de <paramref name="now"/> (o null si ninguna).</summary>
    public static DateTimeOffset? LatestSlotAtOrBefore(ScheduledJob job, DateTimeOffset now, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        switch (job.Recurrence)
        {
            case RecurrenceKind.Once:
                return job.RunAt <= now ? job.RunAt : null;

            case RecurrenceKind.Daily:
            {
                var today = LocalDate(now, tz);
                var slot = SlotOnDate(job, today, tz);
                if (slot > now) slot = SlotOnDate(job, today.AddDays(-1), tz); // la de hoy aún no llega → la de ayer
                return slot >= job.RunAt ? slot : null;
            }

            case RecurrenceKind.Weekly:
            {
                if (job.Weekdays == Weekdays.None) return null;
                var start = LocalDate(now, tz);
                for (int i = 0; i < 8; i++)                  // retrocede hasta una semana buscando día válido
                {
                    var date = start.AddDays(-i);
                    var slot = SlotOnDate(job, date, tz);
                    if (slot <= now && slot >= job.RunAt && Includes(job.Weekdays, date.DayOfWeek))
                        return slot;
                }
                return null;
            }
        }
        return null;
    }

    /// <summary>La próxima franja estrictamente después de <paramref name="now"/> (para "próxima ejecución").</summary>
    public static DateTimeOffset? NextSlotAfter(ScheduledJob job, DateTimeOffset now, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        if (job.RunAt > now) return job.RunAt; // la primera ocurrencia aún no ha llegado

        switch (job.Recurrence)
        {
            case RecurrenceKind.Once:
                return null; // única y ya pasó

            case RecurrenceKind.Daily:
            {
                var date = LocalDate(now, tz);
                for (int i = 0; i <= 1; i++)
                {
                    var slot = SlotOnDate(job, date.AddDays(i), tz);
                    if (slot > now) return slot;
                }
                return null;
            }

            case RecurrenceKind.Weekly:
            {
                if (job.Weekdays == Weekdays.None) return null;
                var date = LocalDate(now, tz);
                for (int i = 0; i < 8; i++)
                {
                    var d = date.AddDays(i);
                    var slot = SlotOnDate(job, d, tz);
                    if (slot > now && Includes(job.Weekdays, d.DayOfWeek)) return slot;
                }
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// La franja del job en la fecha <paramref name="date"/> (a su hora del día, en la zona <paramref name="tz"/>),
    /// o null si ese día no le toca. Útil para listar "las grabaciones de hoy".
    /// <para><paramref name="requireAfterAnchor"/>: si es true (por defecto) NO devuelve franjas anteriores
    /// a la primera ocurrencia (<c>RunAt</c>). Para una vista de "programado HOY" conviene false: que una
    /// tarea recurrente que cae hoy por su REGLA (día+hora) aparezca aunque su hora ya pasara hoy — el ancla
    /// solo importa para disparar, no para mostrar.</para>
    /// </summary>
    public static DateTimeOffset? OccurrenceOnDate(ScheduledJob job, DateOnly date, TimeZoneInfo? tz = null, bool requireAfterAnchor = true)
    {
        tz ??= TimeZoneInfo.Local;
        switch (job.Recurrence)
        {
            case RecurrenceKind.Once:
                return LocalDate(job.RunAt, tz) == date ? job.RunAt : null;

            case RecurrenceKind.Daily:
            {
                var slot = SlotOnDate(job, date, tz);
                return !requireAfterAnchor || slot >= job.RunAt ? slot : null;
            }

            case RecurrenceKind.Weekly:
            {
                if (job.Weekdays == Weekdays.None || !Includes(job.Weekdays, date.DayOfWeek)) return null;
                var slot = SlotOnDate(job, date, tz);
                return !requireAfterAnchor || slot >= job.RunAt ? slot : null;
            }
        }
        return null;
    }

    /// <summary>Fecha del instante <paramref name="instant"/> en la zona <paramref name="tz"/> (su "día local").</summary>
    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo tz)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, tz).DateTime);

    // Franja = la fecha indicada, a la hora del día del job, con el offset que la ZONA tiene ESE día (no el
    // fijo del trabajo): así el offset lo decide el DST vigente en esa fecha y la hora local se mantiene.
    private static DateTimeOffset SlotOnDate(ScheduledJob job, DateOnly date, TimeZoneInfo tz)
    {
        var localWall = new DateTime(date.Year, date.Month, date.Day,
            job.RunAt.Hour, job.RunAt.Minute, job.RunAt.Second, DateTimeKind.Unspecified);
        return new DateTimeOffset(localWall, tz.GetUtcOffset(localWall));
    }
}
