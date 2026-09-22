using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Localization;

namespace Baioss.Record.Application.Scheduling;

/// <summary>
/// Lo que rellena quien crea o edita una tarea automática, venga de la ventana «Programación» de la aplicación o del
/// panel web por la API. Las HORAS son horas del día del equipo que graba (su zona horaria), no instantes: «diaria a las
/// 20:00» tiene que seguir siendo las 20:00 locales todo el año, aunque cambie el horario de verano.
/// </summary>
/// <param name="Date">Fecha de la grabación; solo se usa (y se exige) con <see cref="RecurrenceKind.Once"/>.</param>
/// <param name="Start">Hora del día de inicio (hh:mm:ss).</param>
/// <param name="End">Hora del día de fin. Menor que la de inicio = termina al día siguiente. Igual = no vale.</param>
/// <param name="Weekdays">Días elegidos; solo con <see cref="RecurrenceKind.Weekly"/>.</param>
/// <param name="SegmentMinutes">Cortar en archivos de N minutos; null = un solo archivo.</param>
public sealed record ScheduleDraft(
    Guid ChannelId,
    string? Title,
    RecurrenceKind Recurrence,
    DateOnly? Date,
    TimeSpan Start,
    TimeSpan End,
    Weekdays Weekdays = Weekdays.None,
    int? SegmentMinutes = null);

/// <summary>Por qué un borrador no se puede guardar. Estable: la API lo expone como código (kebab-case).</summary>
public enum ScheduleProblem
{
    InvalidTime,            // una hora fuera de 00:00:00–23:59:59
    EndEqualsStart,         // fin == inicio: no hay nada que grabar
    SegmentMinutesInvalid,  // segmentar cada 0 (o menos) minutos
    PickWeekday,            // semanal sin ningún día
    PickDate,               // «una vez» sin fecha
    PastDate,               // «una vez» en el pasado
    DurationOverlapsNext,   // dura tanto que alcanza la siguiente ocurrencia (que se perdería en silencio)
    DuplicateTitle,         // los títulos son únicos: los archivos se nombran por título
    Clash,                  // se solapa con otra tarea ACTIVA del mismo canal
}

/// <summary>Avisos que no impiden guardar.</summary>
public enum ScheduleNote
{
    SegmentsSingleFile,     // los segmentos son ≥ que la grabación: saldrá un solo archivo
    EndsNextDay,            // cruza la medianoche
}

/// <summary>Resultado de <see cref="SchedulePlanner.Plan"/>: la tarea lista para guardar, o el problema.</summary>
public sealed record SchedulePlan
{
    public ScheduledJob? Job { get; init; }
    public ScheduleProblem? Problem { get; init; }
    /// <summary>Con <see cref="ScheduleProblem.Clash"/>: la tarea con la que choca.</summary>
    public ScheduledJob? ClashWith { get; init; }
    /// <summary>Duración calculada (fin − inicio), también cuando hay problema: los mensajes la citan.</summary>
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<ScheduleNote> Notes { get; init; } = Array.Empty<ScheduleNote>();
    public bool Ok => Job is not null && Problem is null;

    internal static SchedulePlan Fail(ScheduleProblem problem, TimeSpan duration = default, ScheduledJob? clash = null)
        => new() { Problem = problem, Duration = duration, ClashWith = clash };
}

/// <summary>
/// LAS reglas de una tarea automática, en un solo sitio y puras (sin estado, sin reloj salvo el que se pasa). Vivían en
/// la vista-modelo de la ventana de la aplicación; al poder gestionarse la programación también desde el panel web, dos
/// copias de estas reglas acabarían divergiendo — y una tarea que una interfaz acepta y la otra rechaza es justo el tipo
/// de fallo que se descubre cuando un programa no se grabó.
/// </summary>
public static class SchedulePlanner
{
    /// <summary>Tope del título: acaba en el nombre del archivo (<c>dd-MM-yyyy_Título</c>).</summary>
    public const int MaxTitleLength = 80;

    /// <param name="existing">TODAS las tareas guardadas (para el título único y los solapes).</param>
    /// <param name="editing">La tarea que se está editando (conserva su Id y su estado de pausa), o null si es nueva.</param>
    public static SchedulePlan Plan(ScheduleDraft draft, IReadOnlyList<ScheduledJob> existing, DateTimeOffset now,
        TimeZoneInfo tz, ScheduledJob? editing = null)
    {
        var day = TimeSpan.FromDays(1);
        if (draft.Start < TimeSpan.Zero || draft.Start >= day || draft.End < TimeSpan.Zero || draft.End >= day)
            return SchedulePlan.Fail(ScheduleProblem.InvalidTime);

        // La duración (auto-stop) es fin − inicio: fin == inicio no es válido y fin < inicio se interpreta como cruce de
        // medianoche (la grabación termina al día siguiente).
        if (draft.End == draft.Start) return SchedulePlan.Fail(ScheduleProblem.EndEqualsStart);
        var duration = DurationFromStartEnd(draft.Start, draft.End);

        if (draft.SegmentMinutes is { } sm && sm <= 0) return SchedulePlan.Fail(ScheduleProblem.SegmentMinutesInvalid, duration);

        var weekdays = draft.Recurrence == RecurrenceKind.Weekly ? draft.Weekdays & Weekdays.EveryDay : Weekdays.None;
        if (draft.Recurrence == RecurrenceKind.Weekly && weekdays == Weekdays.None)
            return SchedulePlan.Fail(ScheduleProblem.PickWeekday, duration);

        DateTimeOffset runAt;
        if (draft.Recurrence == RecurrenceKind.Once)
        {
            if (draft.Date is not { } d) return SchedulePlan.Fail(ScheduleProblem.PickDate, duration);
            var wall = new DateTime(d.Year, d.Month, d.Day, draft.Start.Hours, draft.Start.Minutes, draft.Start.Seconds, DateTimeKind.Unspecified);
            runAt = new DateTimeOffset(wall, tz.GetUtcOffset(wall)); // offset del DST vigente ESE día
            if (runAt <= now) return SchedulePlan.Fail(ScheduleProblem.PastDate, duration);
        }
        else
        {
            // Primera ocurrencia FUTURA: si la hora de hoy ya pasó, empieza el próximo día válido (evita que una tarea
            // recién creada arranque un trozo de inmediato).
            runAt = ScheduleValidator.NextRecurringAnchor(draft.Recurrence, weekdays, draft.Start, tz, now);
        }

        var title = (draft.Title ?? "").Trim();
        if (title.Length == 0) title = Localizer.T("Sch_DefaultTitle");
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength].Trim();

        var job = new ScheduledJob
        {
            Id = editing?.Id ?? Guid.NewGuid(),      // al editar conserva el mismo Id (es la misma tarea)
            ChannelId = draft.ChannelId,
            Action = ScheduledAction.StartRecording,
            Title = title,
            RunAt = runAt,
            Recurrence = draft.Recurrence,
            Weekdays = weekdays,
            Duration = duration,
            SegmentMinutes = draft.SegmentMinutes is > 0 ? draft.SegmentMinutes : null,
            Enabled = editing?.Enabled ?? true,       // editar no reactiva sola una tarea en pausa
        };

        // La duración no puede alcanzar la siguiente ocurrencia (si no, esa se perdería en silencio).
        if (!ScheduleValidator.DurationFitsInterval(job)) return SchedulePlan.Fail(ScheduleProblem.DurationOverlapsNext, duration);

        // Nombre ÚNICO: los archivos se nombran por título; dos iguales se confundirían. Sin distinguir mayúsculas; al
        // editar no choca consigo misma.
        if (existing.Any(e => e.Id != job.Id && string.Equals(e.Title?.Trim(), job.Title, StringComparison.OrdinalIgnoreCase)))
            return SchedulePlan.Fail(ScheduleProblem.DuplicateTitle, duration) with { Job = job };

        // No solapar con otra tarea ACTIVA del mismo canal (doble reserva).
        if (existing.FirstOrDefault(e => e.Enabled && ScheduleValidator.Overlaps(job, e, now)) is { } clash)
            return SchedulePlan.Fail(ScheduleProblem.Clash, duration, clash) with { Job = job };

        var notes = new List<ScheduleNote>(2);
        if (job.SegmentMinutes is { } m && TimeSpan.FromMinutes(m) >= duration) notes.Add(ScheduleNote.SegmentsSingleFile);
        if (ScheduleValidator.SpansToNextDay(job)) notes.Add(ScheduleNote.EndsNextDay);
        return new SchedulePlan { Job = job, Duration = duration, Notes = notes };
    }

    /// <summary>Duración inicio→fin; si fin &lt; inicio se asume cruce de medianoche (fin del día siguiente).</summary>
    public static TimeSpan DurationFromStartEnd(TimeSpan start, TimeSpan end)
        => end > start ? end - start
         : end < start ? end + TimeSpan.FromDays(1) - start
         : TimeSpan.Zero;

    /// <summary>Hora del día a la que TERMINA una tarea (inicio + duración, normalizado a 24 h), en la zona dada.</summary>
    public static TimeSpan EndTimeOfDay(ScheduledJob job, TimeZoneInfo tz)
    {
        var start = TimeZoneInfo.ConvertTime(job.RunAt, tz).TimeOfDay;
        return TimeSpan.FromTicks((start + (job.Duration ?? TimeSpan.Zero)).Ticks % TimeSpan.FromDays(1).Ticks);
    }
}

/// <summary>Los textos de la programación que comparten la ventana de la aplicación y la API (en el idioma vigente).</summary>
public static class ScheduleText
{
    /// <summary>El problema en palabras. <paramref name="channelKey"/>: la letra del canal, para el mensaje de choque.</summary>
    public static string Describe(SchedulePlan plan, string? channelKey)
        => plan.Problem switch
        {
            ScheduleProblem.InvalidTime => Localizer.T("Sch_Err_InvalidTime"),
            ScheduleProblem.EndEqualsStart => Localizer.T("Sch_Err_EndEqualsStart"),
            ScheduleProblem.SegmentMinutesInvalid => Localizer.T("Sch_Err_SegmentMinutes"),
            ScheduleProblem.PickWeekday => Localizer.T("Sch_Err_PickWeekday"),
            ScheduleProblem.PickDate => Localizer.T("Sch_Err_PickDate"),
            ScheduleProblem.PastDate => Localizer.T("Sch_Err_PastDate"),
            ScheduleProblem.DurationOverlapsNext => Localizer.F("Sch_Err_DurationOverlaps",
                Duration(plan.Duration), Interval(plan.Job is { } j ? ScheduleValidator.RecurrenceInterval(j) : null)),
            ScheduleProblem.DuplicateTitle => Localizer.F("Sch_Err_DuplicateTitle", plan.Job?.Title ?? ""),
            ScheduleProblem.Clash => Localizer.F("Sch_Err_Clash", plan.ClashWith?.Title ?? "", channelKey ?? "?"),
            _ => "",
        };

    /// <summary>«1 h 30 min», «45 s».</summary>
    public static string Duration(TimeSpan d)
    {
        var parts = new List<string>(3);
        if ((int)d.TotalHours > 0) parts.Add($"{(int)d.TotalHours} h");
        if (d.Minutes > 0) parts.Add($"{d.Minutes} min");
        if (d.Seconds > 0) parts.Add($"{d.Seconds} s");
        return parts.Count > 0 ? string.Join(" ", parts) : "0 s";
    }

    public static string Interval(TimeSpan? interval)
        => interval is not { } t ? "—"
           : (int)t.TotalDays == 1 ? Localizer.T("Sch_Interval_Daily")
           : t.TotalDays >= 1 ? Localizer.F("Sch_Interval_Days", (int)t.TotalDays)
           : Localizer.F("Sch_Interval_Hours", (int)t.TotalHours);
}
