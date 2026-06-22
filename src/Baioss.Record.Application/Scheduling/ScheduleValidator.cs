using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Scheduling;

/// <summary>
/// Validaciones PURAS (sin estado ni reloj salvo el que se pasa) de las grabaciones programadas:
/// que la duración no alcance la siguiente ocurrencia, que dos tareas del mismo canal no se solapen,
/// el cálculo de la primera ocurrencia futura y si una grabación cruza la medianoche.
/// </summary>
public static class ScheduleValidator
{
    /// <summary>
    /// Hueco mínimo entre ocurrencias consecutivas. Diaria = 24 h; semanal = menor separación entre los
    /// días elegidos (L-M = 24 h, L-X-V = 48 h, solo-L = 7 días); única = null (no se repite).
    /// </summary>
    public static TimeSpan? RecurrenceInterval(ScheduledJob job)
    {
        switch (job.Recurrence)
        {
            case RecurrenceKind.Once: return null;
            case RecurrenceKind.Daily: return TimeSpan.FromDays(1);
            case RecurrenceKind.Weekly:
                var days = SelectedDays(job.Weekdays);
                if (days.Count == 0) return null;
                if (days.Count == 1) return TimeSpan.FromDays(7);
                int minGap = 7;
                for (int i = 0; i < days.Count; i++)
                {
                    int cur = (int)days[i];
                    int next = (int)days[(i + 1) % days.Count];
                    int gap = (next - cur + 7) % 7;
                    if (gap == 0) gap = 7;
                    minGap = Math.Min(minGap, gap);
                }
                return TimeSpan.FromDays(minGap);
            default: return null;
        }
    }

    /// <summary>(1) La duración no debe alcanzar la siguiente ocurrencia (si no, esa se perdería).</summary>
    public static bool DurationFitsInterval(ScheduledJob job)
    {
        if (job.Duration is not { } d) return true;             // sin auto-stop: no aplica
        var interval = RecurrenceInterval(job);
        return interval is null || d < interval.Value;
    }

    /// <summary>(2) ¿Se solapan dos tareas del MISMO canal? Compara intervalos de sus próximas ocurrencias.</summary>
    public static bool Overlaps(ScheduledJob a, ScheduledJob b, DateTimeOffset from, int sample = 60)
    {
        if (a.ChannelId != b.ChannelId || a.Id == b.Id) return false;
        var sa = Occurrences(a, from, sample);
        var sb = Occurrences(b, from, sample);
        foreach (var (s1, e1) in sa)
            foreach (var (s2, e2) in sb)
                if (s1 < e2 && s2 < e1) return true;            // dos intervalos se tocan
        return false;
    }

    /// <summary>(5) ¿La grabación termina en una fecha posterior a la de inicio (cruza la medianoche)?</summary>
    public static bool SpansToNextDay(ScheduledJob job)
        => job.Duration is { } d && (job.RunAt + d).Date > job.RunAt.Date;

    /// <summary>
    /// (3) Primera ocurrencia FUTURA de una repetición: si la hora de hoy ya pasó, empieza el próximo día
    /// válido (evita que una tarea recién creada arranque un trozo de inmediato).
    /// </summary>
    public static DateTimeOffset NextRecurringAnchor(RecurrenceKind kind, Weekdays days, TimeSpan timeOfDay, TimeSpan offset, DateTimeOffset now)
    {
        var startDate = DateOnly.FromDateTime(now.ToOffset(offset).DateTime);
        for (int i = 0; i < 8; i++)
        {
            var date = startDate.AddDays(i);
            var slot = Slot(date, timeOfDay, offset);
            if (slot <= now) continue;
            if (kind == RecurrenceKind.Daily) return slot;
            if (kind == RecurrenceKind.Weekly && ScheduleEvaluator.Includes(days, date.DayOfWeek)) return slot;
        }
        return Slot(startDate, timeOfDay, offset); // fallback teórico
    }

    private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Occurrences(ScheduledJob job, DateTimeOffset from, int count)
    {
        var dur = job.Duration ?? TimeSpan.FromHours(1); // sin duración: ventana nominal de 1 h para el chequeo
        var list = new List<(DateTimeOffset, DateTimeOffset)>();

        if (job.Recurrence == RecurrenceKind.Once)
        {
            if (job.RunAt + dur > from) list.Add((job.RunAt, job.RunAt + dur));
            return list;
        }

        var cursor = from < job.RunAt ? job.RunAt.AddTicks(-1) : from;
        for (int i = 0; i < count; i++)
        {
            if (ScheduleEvaluator.NextSlotAfter(job, cursor) is not { } s) break;
            list.Add((s, s + dur));
            cursor = s;
        }
        return list;
    }

    private static DateTimeOffset Slot(DateOnly date, TimeSpan tod, TimeSpan offset)
        => new(date.Year, date.Month, date.Day, tod.Hours, tod.Minutes, tod.Seconds, offset);

    private static List<DayOfWeek> SelectedDays(Weekdays w)
    {
        var list = new List<DayOfWeek>();
        if (w.HasFlag(Weekdays.Monday)) list.Add(DayOfWeek.Monday);
        if (w.HasFlag(Weekdays.Tuesday)) list.Add(DayOfWeek.Tuesday);
        if (w.HasFlag(Weekdays.Wednesday)) list.Add(DayOfWeek.Wednesday);
        if (w.HasFlag(Weekdays.Thursday)) list.Add(DayOfWeek.Thursday);
        if (w.HasFlag(Weekdays.Friday)) list.Add(DayOfWeek.Friday);
        if (w.HasFlag(Weekdays.Saturday)) list.Add(DayOfWeek.Saturday);
        if (w.HasFlag(Weekdays.Sunday)) list.Add(DayOfWeek.Sunday);
        list.Sort();
        return list;
    }
}
