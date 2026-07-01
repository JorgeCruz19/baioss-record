using System.Globalization;
using System.Text.Json;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Scheduling;

/// <summary>
/// Importa una programación desde el JSON que produce <see cref="ScheduleExporter.ToJson"/>. PURO y testeable:
/// reconstruye los <see cref="ScheduledJob"/> del arreglo «schedule» y devuelve también los errores por entrada.
/// La UI valida (canal existe en este equipo, título único) y los añade al scheduler. Los <c>Id</c> se generan
/// NUEVOS (importar = añadir, no restaurar), y los campos calculados (nextRun) se ignoran.
/// </summary>
public static class ScheduleImporter
{
    public sealed record ImportResult(IReadOnlyList<ScheduledJob> Jobs, IReadOnlyList<string> Errors);

    public static ImportResult FromJson(string json)
    {
        var jobs = new List<ScheduledJob>();
        var errors = new List<string>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return new ImportResult(jobs, new[] { "El archivo no es JSON válido: " + ex.Message }); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("schedule", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new ImportResult(jobs, new[] { "No parece un archivo de programación exportado (falta el arreglo «schedule»)." });

            int i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                i++;
                try { jobs.Add(ParseJob(el)); }
                catch (Exception ex) { errors.Add($"Entrada {i}: {ex.Message}"); }
            }
        }
        return new ImportResult(jobs, errors);
    }

    private static ScheduledJob ParseJob(JsonElement el)
    {
        var channelId = GetGuid(el, "channelId") ?? throw new FormatException("«channelId» ausente o inválido.");
        var runAt = GetDate(el, "runAt") ?? throw new FormatException("«runAt» ausente o inválido.");

        var weekdays = Weekdays.None;
        if (el.TryGetProperty("weekdays", out var wd) && wd.ValueKind == JsonValueKind.Array)
            foreach (var d in wd.EnumerateArray())
                if (Enum.TryParse<Weekdays>(d.GetString(), ignoreCase: true, out var flag)) weekdays |= flag;

        return new ScheduledJob
        {
            ChannelId = channelId,
            Action = GetEnum(el, "action", ScheduledAction.StartRecording),
            Title = GetString(el, "title") ?? "Grabación programada",
            RunAt = runAt,
            Recurrence = GetEnum(el, "recurrence", RecurrenceKind.Once),
            Weekdays = weekdays,
            Duration = GetInt(el, "durationMinutes") is { } m ? TimeSpan.FromMinutes(m) : null,
            SegmentMinutes = GetInt(el, "segmentMinutes"),
            ProfileId = GetGuid(el, "profileId"),
            InputSourceId = GetGuid(el, "inputSourceId"),
            Enabled = GetBool(el, "enabled") ?? true,
        };
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static Guid? GetGuid(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var g) ? g : null;

    private static DateTimeOffset? GetDate(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
           DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static bool? GetBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

    private static T GetEnum<T>(JsonElement el, string name, T fallback) where T : struct, Enum
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
           Enum.TryParse<T>(p.GetString(), ignoreCase: true, out var v) ? v : fallback;
}
