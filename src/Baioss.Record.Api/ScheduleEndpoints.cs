using System.Globalization;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Scheduling;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Baioss.Record.Api;

/// <summary>
/// La PROGRAMACIÓN (tareas automáticas de grabación de cada canal) por la API, para gestionarla desde el panel web igual
/// que desde la ventana «🕒 Programación» de la aplicación: listar, crear, editar, pausar/reanudar, borrar y saltar la que
/// está en marcha. Las reglas NO están aquí sino en <see cref="SchedulePlanner"/>, las mismas que aplica la aplicación.
///
/// HORAS: todo lo que entra y sale son horas de PARED del equipo que graba (<c>"20:00:00"</c>, <c>"2026-09-20T20:00:00"</c>,
/// sin zona). Una tarea «diaria a las 20:00» son las 20:00 de ese equipo todo el año; convertirlas a instantes en el
/// navegador (que puede estar en otra zona, o cambiar de horario otro día) solo serviría para descuadrarlas.
/// </summary>
public static class ScheduleEndpoints
{
    public sealed record ScheduleBody(
        Guid ChannelId, string? Title, string? Recurrence, string? Date, string? StartTime, string? EndTime,
        string[]? Weekdays, int? SegmentMinutes, string? Operator);

    public sealed record EnabledBody(bool Enabled, string? Operator);

    public static void MapBaiossSchedule(this RouteGroupBuilder api)
    {
        // Todas las tareas (o las de un canal), con lo que el panel necesita ya calculado: próxima ejecución, estado,
        // la ocurrencia de HOY y las que están en marcha. «now» es el reloj del equipo que graba.
        api.MapGet("/schedule", async (Guid? channel, HttpContext http, CancellationToken ct) =>
        {
            if (Services(http) is not { } s) return NoScheduler();
            var now = s.Clock.UtcNow;
            var jobs = await s.Scheduler.GetAllAsync(ct);
            var active = s.Scheduler.ActiveRecordings;
            var local = TimeZoneInfo.ConvertTime(now, s.Zone);
            return Results.Ok(new
            {
                now = Wall(local),
                utcOffsetMinutes = (int)local.Offset.TotalMinutes,
                jobs = jobs.Where(j => channel is null || j.ChannelId == channel)
                    .OrderBy(j => s.Keys.GetValueOrDefault(j.ChannelId) ?? "~", StringComparer.Ordinal)
                    .ThenBy(j => ScheduleEvaluator.NextSlotAfter(j, now, s.Zone) ?? DateTimeOffset.MaxValue)
                    .ThenBy(j => j.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(j => ToDto(j, now, s, active)),
                active = active.Where(a => channel is null || a.ChannelId == channel).Select(a => ToDto(a, now, s)),
            });
        });

        // Solo lo que está EN MARCHA (para la tarjeta de cada canal): ligero, se puede sondear a menudo.
        api.MapGet("/schedule/active", (HttpContext http) =>
        {
            if (Services(http) is not { } s) return NoScheduler();
            var now = s.Clock.UtcNow;
            return Results.Ok(s.Scheduler.ActiveRecordings.Select(a => ToDto(a, now, s)));
        });

        api.MapPost("/schedule", async (ScheduleBody body, HttpContext http, CancellationToken ct) =>
        {
            if (Services(http) is not { } s) return NoScheduler();
            return await SaveAsync(s, body, editingId: null, ct);
        });

        api.MapPut("/schedule/{id:guid}", async (Guid id, ScheduleBody body, HttpContext http, CancellationToken ct) =>
        {
            if (Services(http) is not { } s) return NoScheduler();
            return await SaveAsync(s, body, id, ct);
        });

        // Pausar / reanudar sin borrar. Reanudar pasa por las mismas comprobaciones de solape que crear: mientras estaba
        // en pausa pudo ocuparse su franja con otra tarea.
        api.MapPost("/schedule/{id:guid}/enabled", async (Guid id, EnabledBody body, HttpContext http, CancellationToken ct) =>
        {
            if (Services(http) is not { } s) return NoScheduler();
            var all = await s.Scheduler.GetAllAsync(ct);
            if (all.FirstOrDefault(j => j.Id == id) is not { } job) return NotFound();
            var now = s.Clock.UtcNow;
            if (body.Enabled && !job.Enabled
                && all.FirstOrDefault(e => e.Enabled && ScheduleValidator.Overlaps(job, e, now)) is { } clash)
            {
                var plan = new SchedulePlan { Job = job, Problem = ScheduleProblem.Clash, ClashWith = clash };
                return Problem(plan, s.Keys.GetValueOrDefault(job.ChannelId));
            }
            await s.Scheduler.SetEnabledAsync(id, body.Enabled, Clean(body.Operator), ct);
            var saved = (await s.Scheduler.GetAllAsync(ct)).FirstOrDefault(j => j.Id == id);
            return saved is null ? NotFound() : Results.Ok(ToDto(saved, now, s, s.Scheduler.ActiveRecordings));
        });

        // Borrar. Si la tarea está grabando AHORA, esa grabación sigue hasta su hora de fin (igual que en la aplicación):
        // para cortarla está «saltar».
        api.MapDelete("/schedule/{id:guid}", async (Guid id, string? @operator, HttpContext http, CancellationToken ct) =>
        {
            if (Services(http) is not { } s) return NoScheduler();
            if ((await s.Scheduler.GetAllAsync(ct)).All(j => j.Id != id)) return NotFound();
            await s.Scheduler.CancelAsync(id, Clean(@operator), ct);
            return Results.NoContent();
        });

        // Saltar la grabación programada EN CURSO de un canal (el ⏏ de la aplicación): la detiene ya y marca SOLO esa
        // ocurrencia como saltada, para que no se reanude; las siguientes (diaria/semanal) siguen.
        api.MapPost("/channels/{id:guid}/schedule/skip", async (Guid id, HttpContext http, CancellationToken ct) =>
        {
            if (Services(http) is not { } s) return NoScheduler();
            return await s.Scheduler.SkipCurrentAsync(id, ct)
                ? Results.NoContent()
                : Results.Conflict(new { error = "Ese canal no tiene ninguna grabación programada en marcha.", code = "no-active-task" });
        });
    }

    // ------------------------------------------------------------------ guardar (crear / editar)

    private static async Task<IResult> SaveAsync(Ctx s, ScheduleBody body, Guid? editingId, CancellationToken ct)
    {
        if (!s.Keys.ContainsKey(body.ChannelId)) return Bad("unknown-channel", "Ese canal no existe en este equipo.");
        if (!TryRecurrence(body.Recurrence, out var recurrence)) return Bad("invalid-recurrence", "La repetición debe ser Once, Daily o Weekly.");
        if (!TryTime(body.StartTime, out var start) || !TryTime(body.EndTime, out var end)) return Bad("invalid-time", "Las horas van como HH:mm o HH:mm:ss.");
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(body.Date))
        {
            if (!DateOnly.TryParseExact(body.Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return Bad("invalid-date", "La fecha va como yyyy-MM-dd.");
            date = d;
        }
        if (!TryWeekdays(body.Weekdays, out var weekdays)) return Bad("invalid-weekdays", "Los días van como Mon, Tue, Wed, Thu, Fri, Sat, Sun.");

        var existing = await s.Scheduler.GetAllAsync(ct);
        ScheduledJob? editing = null;
        if (editingId is { } id && (editing = existing.FirstOrDefault(j => j.Id == id)) is null) return NotFound();

        var draft = new ScheduleDraft(body.ChannelId, body.Title, recurrence, date, start, end, weekdays, body.SegmentMinutes);
        var now = s.Clock.UtcNow;
        var plan = SchedulePlanner.Plan(draft, existing, now, s.Zone, editing);
        if (!plan.Ok) return Problem(plan, s.Keys.GetValueOrDefault(body.ChannelId));

        var job = plan.Job!;
        if (editing is null) await s.Scheduler.ScheduleAsync(job, Clean(body.Operator), ct);
        else await s.Scheduler.UpdateAsync(job, Clean(body.Operator), ct);

        var dto = new { job = ToDto(job, now, s, s.Scheduler.ActiveRecordings), notes = plan.Notes.Select(n => Kebab(n.ToString())) };
        return editing is null ? Results.Created($"/api/v1/schedule/{job.Id}", dto) : Results.Ok(dto);
    }

    // ------------------------------------------------------------------ formas de salida

    private static object ToDto(ScheduledJob j, DateTimeOffset now, Ctx s, IReadOnlyList<ActiveScheduledRecording> active)
    {
        var first = TimeZoneInfo.ConvertTime(j.RunAt, s.Zone);
        var running = active.FirstOrDefault(a => a.JobId == j.Id);
        var next = j.Enabled ? ScheduleEvaluator.NextSlotAfter(j, now, s.Zone) : null;

        // La ocurrencia de HOY (por su regla: aunque su hora ya pasara) con el mismo estado que enseña la aplicación.
        object? today = null;
        var todayDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, s.Zone).DateTime);
        if (j.Enabled && ScheduleEvaluator.OccurrenceOnDate(j, todayDate, s.Zone, requireAfterAnchor: false) is { } slot)
        {
            DateTimeOffset? end = j.Duration is { } d ? slot + d : null;
            string status = j.SkippedOccurrence == slot ? "skipped"
                : now < slot ? "scheduled"
                : end is { } e && now >= e ? "recorded"
                : "running";
            today = new { start = Wall(slot, s.Zone), end = end is { } e2 ? Wall(e2, s.Zone) : null, status };
        }

        return new
        {
            id = j.Id,
            channelId = j.ChannelId,
            channelKey = s.Keys.GetValueOrDefault(j.ChannelId),
            title = j.Title,
            recurrence = j.Recurrence.ToString(),
            weekdays = WeekdayCodes(j.Weekdays),
            date = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            startTime = first.TimeOfDay.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            endTime = SchedulePlanner.EndTimeOfDay(j, s.Zone).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            durationSeconds = j.Duration is { } dur ? (int)dur.TotalSeconds : (int?)null,
            endsNextDay = ScheduleValidator.SpansToNextDay(j),
            segmentMinutes = j.SegmentMinutes is > 0 ? j.SegmentMinutes : null,
            enabled = j.Enabled,
            // running = la está grabando AHORA · paused · done = única y ya pasó · scheduled = tiene una próxima ejecución
            state = running is not null ? "running" : !j.Enabled ? "paused" : next is null ? "done" : "scheduled",
            nextRun = next is { } n ? Wall(n, s.Zone) : null,
            lastRun = j.LastRunAt is { } lr ? Wall(lr, s.Zone) : null,
            runningUntil = running is not null ? Wall(running.EndsAt, s.Zone) : null,
            today,
        };
    }

    private static object ToDto(ActiveScheduledRecording a, DateTimeOffset now, Ctx s) => new
    {
        jobId = a.JobId,
        channelId = a.ChannelId,
        channelKey = s.Keys.GetValueOrDefault(a.ChannelId),
        sessionId = a.SessionId,
        title = a.Title,
        startedAt = Wall(a.StartedAt, s.Zone),
        endsAt = Wall(a.EndsAt, s.Zone),
        // Calculado AQUÍ, con el reloj del equipo que graba: el del navegador puede ir desfasado o estar en otra zona.
        remainingSeconds = Math.Max(0, (int)(a.EndsAt - now).TotalSeconds),
    };

    // ------------------------------------------------------------------ ayudas

    private sealed record Ctx(ISchedulerService Scheduler, IClock Clock, TimeZoneInfo Zone, IReadOnlyDictionary<Guid, string> Keys);

    /// <summary>Lo que necesitan estos endpoints, resuelto A MANO: un host sin programador (tests, despliegue mínimo) no
    /// debe fallar al construir la API entera por un parámetro de servicio que no existe.</summary>
    private static Ctx? Services(HttpContext http)
    {
        var scheduler = http.RequestServices.GetService<ISchedulerService>();
        var clock = http.RequestServices.GetService<IClock>();
        var channels = http.RequestServices.GetService<IChannelManager>();
        if (scheduler is null || clock is null || channels is null) return null;
        var keys = channels.Channels.ToDictionary(c => c.ChannelId, c => c.Status.Key);
        return new Ctx(scheduler, clock, http.RequestServices.GetService<TimeZoneInfo>() ?? TimeZoneInfo.Local, keys);
    }

    private static IResult NoScheduler() => Results.NotFound(new { error = "Este equipo no ofrece programación.", code = "no-scheduler" });
    private static IResult NotFound() => Results.NotFound(new { error = "Esa tarea ya no existe.", code = "not-found" });
    private static IResult Bad(string code, string error) => Results.BadRequest(new { error, code });

    /// <summary>Un borrador que no pasa las reglas: 409 si choca con lo que ya hay (título repetido, solape), 400 si está mal.</summary>
    private static IResult Problem(SchedulePlan plan, string? channelKey)
    {
        var payload = new
        {
            error = ScheduleText.Describe(plan, channelKey),
            code = Kebab(plan.Problem!.Value.ToString()),
            clashWith = plan.ClashWith?.Title,
        };
        return plan.Problem is ScheduleProblem.DuplicateTitle or ScheduleProblem.Clash ? Results.Conflict(payload) : Results.BadRequest(payload);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Hora de pared (sin zona) en la zona del equipo que graba: «2026-09-20T20:00:00».</summary>
    private static string Wall(DateTimeOffset instant, TimeZoneInfo zone) => Wall(TimeZoneInfo.ConvertTime(instant, zone));
    private static string Wall(DateTimeOffset local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool TryRecurrence(string? text, out RecurrenceKind kind)
        => Enum.TryParse(text?.Trim(), ignoreCase: true, out kind) && Enum.IsDefined(kind) && !int.TryParse(text, out _);

    private static bool TryTime(string? text, out TimeSpan time)
    {
        time = default;
        foreach (var format in new[] { @"hh\:mm\:ss", @"hh\:mm" })
            if (TimeSpan.TryParseExact(text?.Trim(), format, CultureInfo.InvariantCulture, out time)) return true;
        return false;
    }

    private static readonly (string Code, Weekdays Flag)[] Days =
    {
        ("Mon", Weekdays.Monday), ("Tue", Weekdays.Tuesday), ("Wed", Weekdays.Wednesday), ("Thu", Weekdays.Thursday),
        ("Fri", Weekdays.Friday), ("Sat", Weekdays.Saturday), ("Sun", Weekdays.Sunday),
    };

    private static string[] WeekdayCodes(Weekdays mask) => Days.Where(d => mask.HasFlag(d.Flag)).Select(d => d.Code).ToArray();

    private static bool TryWeekdays(string[]? codes, out Weekdays mask)
    {
        mask = Weekdays.None;
        foreach (var raw in codes ?? Array.Empty<string>())
        {
            var code = raw?.Trim() ?? "";
            var day = Days.FirstOrDefault(d => code.StartsWith(d.Code, StringComparison.OrdinalIgnoreCase) && code.Length >= 3);
            if (day.Code is null || (code.Length > 3 && !day.Flag.ToString().Equals(code, StringComparison.OrdinalIgnoreCase))) return false;
            mask |= day.Flag;
        }
        return true;
    }

    /// <summary>«DurationOverlapsNext» → «duration-overlaps-next»: el código estable que ve el cliente.</summary>
    private static string Kebab(string pascal)
        => string.Concat(pascal.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
}
