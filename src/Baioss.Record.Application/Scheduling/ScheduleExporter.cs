using System.Text;
using System.Text.Json;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Scheduling;

/// <summary>
/// Exporta la programación de grabaciones a <b>CSV</b> (revisión en Excel) o <b>JSON</b> (copia de
/// seguridad / reimport). Funciones PURAS y testeables: reciben los trabajos, un resolutor de clave de
/// canal y el instante «ahora» (para calcular la próxima ejecución con <see cref="ScheduleEvaluator"/>).
/// La UI se ocupa del diálogo de archivo y de escribir el contenido.
/// </summary>
public static class ScheduleExporter
{
    private static readonly (Weekdays Flag, string Short)[] DayOrder =
    {
        (Weekdays.Monday, "L"), (Weekdays.Tuesday, "M"), (Weekdays.Wednesday, "X"), (Weekdays.Thursday, "J"),
        (Weekdays.Friday, "V"), (Weekdays.Saturday, "S"), (Weekdays.Sunday, "D"),
    };

    private static string ActionLabel(ScheduledAction a) => a switch
    {
        ScheduledAction.StartRecording => "Grabar",
        ScheduledAction.StopRecording => "Detener",
        ScheduledAction.SwitchProfile => "Cambiar perfil",
        ScheduledAction.SwitchSource => "Cambiar fuente",
        _ => a.ToString(),
    };

    public static string WeekdaysLabel(Weekdays days)
    {
        if (days == Weekdays.None) return "";
        if ((days & Weekdays.EveryDay) == Weekdays.EveryDay) return "Todos";
        return string.Join(",", DayOrder.Where(d => (days & d.Flag) != 0).Select(d => d.Short));
    }

    private static string RecurrenceLabel(ScheduledJob j) => j.Recurrence switch
    {
        RecurrenceKind.Once => "Una vez",
        RecurrenceKind.Daily => "Cada día",
        RecurrenceKind.Weekly => WeekdaysLabel(j.Weekdays) is { Length: > 0 } w ? $"Semanal: {w}" : "Semanal",
        _ => j.Recurrence.ToString(),
    };

    // Once → fecha+hora; recurrente → solo la hora del día (en el offset del propio job).
    private static string StartLabel(ScheduledJob j) =>
        j.Recurrence == RecurrenceKind.Once ? j.RunAt.ToString("yyyy-MM-dd HH:mm:ss") : j.RunAt.ToString("HH:mm:ss");

    /// <summary>CSV con BOM (Excel muestra los acentos) y separador «;» (lista de Excel en es-ES).</summary>
    public static string ToCsv(IReadOnlyList<ScheduledJob> jobs, Func<Guid, string?> channelKey, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // BOM UTF-8: Excel muestra los acentos correctamente
        sb.Append("Canal;Título;Acción;Repetición;Inicio;Duración (min);Segmentar (min);Activo;Próxima ejecución\r\n");
        foreach (var j in Ordered(jobs, channelKey))
        {
            var next = ScheduleEvaluator.NextSlotAfter(j, now);
            string[] cells =
            {
                channelKey(j.ChannelId) ?? j.ChannelId.ToString(),
                j.Title,
                ActionLabel(j.Action),
                RecurrenceLabel(j),
                StartLabel(j),
                j.Duration is { } d ? ((int)d.TotalMinutes).ToString() : "",
                j.SegmentMinutes is { } sm && sm > 0 ? sm.ToString() : "",
                j.Enabled ? "Sí" : "No",
                next?.ToString("yyyy-MM-dd HH:mm") ?? "—",
            };
            sb.Append(string.Join(";", cells.Select(CsvCell))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string CsvCell(string value)
        => value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>JSON indentado y reimportable (canal, acción, recurrencia, días, hora, duración, segmentación…).</summary>
    public static string ToJson(IReadOnlyList<ScheduledJob> jobs, Func<Guid, string?> channelKey, DateTimeOffset now)
    {
        var schedule = Ordered(jobs, channelKey).Select(j => new
        {
            channel = channelKey(j.ChannelId),
            channelId = j.ChannelId,
            title = j.Title,
            action = j.Action.ToString(),
            recurrence = j.Recurrence.ToString(),
            weekdays = DayOrder.Where(d => (j.Weekdays & d.Flag) != 0).Select(d => d.Flag.ToString()).ToArray(),
            runAt = j.RunAt,
            durationMinutes = j.Duration is { } d ? (int?)(int)d.TotalMinutes : null,
            segmentMinutes = j.SegmentMinutes,
            profileId = j.ProfileId,
            inputSourceId = j.InputSourceId,
            enabled = j.Enabled,
            nextRun = ScheduleEvaluator.NextSlotAfter(j, now),
        });
        return JsonSerializer.Serialize(
            new { exportedAt = now, count = jobs.Count, schedule },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<ScheduledJob> Ordered(IReadOnlyList<ScheduledJob> jobs, Func<Guid, string?> channelKey)
        => jobs.OrderBy(j => channelKey(j.ChannelId) ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(j => j.RunAt);
}
