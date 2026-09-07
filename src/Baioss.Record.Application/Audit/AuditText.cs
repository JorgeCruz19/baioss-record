using System.Globalization;
using System.Text.Json;
using Baioss.Record.Application.Localization;

namespace Baioss.Record.Application.Audit;

/// <summary>
/// Convierte una entrada del registro de auditoría en algo que un supervisor pueda leer.
///
/// Lo que hay guardado es el <c>ToString()</c> del evento —«RecordingStopped { OccurredAt = …, ChannelId = …,
/// Reason = DiskFull, … }»—, que es correcto para diagnosticar pero ilegible en una tabla. Aquí se traduce el
/// TIPO de evento y se compone un DETALLE en palabras a partir de la copia estructurada (<c>PayloadJson</c>).
///
/// Vive en la capa Application, no en la de interfaz, por lo mismo que el catálogo de idiomas: para poder
/// probarlo sin arrastrar WPF (el proyecto de tests es <c>net8.0</c>).
/// </summary>
public static class AuditText
{
    /// <summary>Nombre legible del tipo de evento. Si no está traducido, se devuelve el nombre técnico tal
    /// cual: es preferible un nombre feo a una fila sin identificar.</summary>
    public static string Category(string category)
    {
        var key = "Audit_Cat_" + category;
        var text = Localizer.T(key);
        return text == key ? category : text;
    }

    /// <summary>
    /// Detalle en palabras. Se compone del <paramref name="payloadJson"/> cuando se conoce la forma del evento;
    /// si no (evento nuevo, JSON ausente o ilegible), se limpia el mensaje técnico dejando solo sus campos.
    /// </summary>
    public static string Detail(string category, string? payloadJson, string message)
    {
        JsonElement? p = null;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try { p = JsonDocument.Parse(payloadJson!).RootElement.Clone(); }
            catch { /* payload ilegible: se cae al mensaje técnico */ }
        }

        if (p is { } j)
        {
            var detail = category switch
            {
                "RecordingStarted" => Started(j),
                "RecordingStopped" => Stopped(j),
                "RecordingStartFailed" => Failed(j),
                "ScheduledRecordingSkipped" => Skipped(j),
                "SegmentCompleted" => Segment(j),
                "StorageLow" => StorageLow(j),
                "StorageEmergencyEntered" or "StorageEmergencyCleared" => StorageEmergency(j),
                "RecordingInterrupted" => Interrupted(j),
                "RecordingFileUnverified" => Unverified(j),
                "StorageStalled" => Localizer.F("Audit_DiskStall_Detail", Str(j, "Volume") ?? "?"),
                "StorageStallCleared" => Localizer.F("Audit_DiskStallCleared_Detail", Str(j, "Volume") ?? "?",
                    Duration(j, "Duration") ?? Str(j, "Duration") ?? "?"),
                _ => null,
            };
            if (detail is not null) return detail;
        }

        return Tidy(message);
    }

    // --- Grabación ---

    private static string Started(JsonElement j)
    {
        var parts = new List<string>(3);
        var trigger = Str(j, "Trigger");
        var job = Str(j, "ScheduledJobTitle");
        parts.Add(trigger switch
        {
            "1" or "Manual" => Localizer.T("Audit_Trigger_Manual"),
            "2" or "Scheduled" => string.IsNullOrEmpty(job)
                ? Localizer.T("Audit_Trigger_Scheduled")
                : Localizer.F("Audit_Trigger_ScheduledNamed", job!),
            "3" or "Api" => Localizer.T("Audit_Trigger_Api"),
            _ => Localizer.T("Audit_Trigger_Unknown"),
        });
        if (Str(j, "Operator") is { Length: > 0 } op) parts.Add(op);
        if (Str(j, "RecordingName") is { Length: > 0 } name) parts.Add(name);
        return string.Join(" · ", parts);
    }

    private static string Stopped(JsonElement j)
    {
        var parts = new List<string>(3) { ReasonText(Str(j, "Reason")) };
        if (Duration(j, "Duration") is { } d) parts.Add(d);
        int files = Int(j, "Files");
        if (files > 0)
        {
            parts.Add(Localizer.F(files == 1 ? "Audit_Files_One" : "Audit_Files_Many", files));
            long bytes = Long(j, "TotalBytes");
            if (bytes > 0) parts.Add(Bytes(bytes));
        }
        return string.Join(" · ", parts);
    }

    private static string Failed(JsonElement j)
    {
        var reason = Str(j, "Reason") ?? "";
        var job = Str(j, "ScheduledJobTitle");
        return string.IsNullOrEmpty(job) ? reason : $"«{job}» · {reason}";
    }

    private static string Skipped(JsonElement j)
    {
        var reason = Str(j, "Reason") ?? "";
        var job = Str(j, "ScheduledJobTitle");
        return string.IsNullOrEmpty(job) ? reason : $"«{job}» · {reason}";
    }

    /// <summary>«27,7 GB libres · quedan 7 h 27 min». El aviso de disco es de los sucesos más frecuentes en
    /// 24/7, así que merece salir en palabras y no como un volcado de bytes y de un TimeSpan.</summary>
    private static string StorageLow(JsonElement j)
    {
        var parts = new List<string>(2) { Localizer.F("Audit_Disk_Free", Bytes(Long(j, "FreeBytes"))) };
        if (Duration(j, "EstimatedRemaining") is { } left) parts.Add(Localizer.F("Audit_Disk_Remaining", left));
        return string.Join(" · ", parts);
    }

    /// <summary>«C:\ · 95 % ocupado · 12,4 GB libres».</summary>
    private static string StorageEmergency(JsonElement j)
    {
        var parts = new List<string>(3);
        if (Str(j, "Volume") is { Length: > 0 } vol) parts.Add(vol);
        if (j.TryGetProperty("UsedPercent", out var pct) && pct.ValueKind == JsonValueKind.Number)
            parts.Add(Localizer.F("Audit_Disk_Used", pct.GetDouble().ToString("0.#", CultureInfo.CurrentCulture)));
        parts.Add(Localizer.F("Audit_Disk_Free", Bytes(Long(j, "FreeBytes"))));
        return string.Join(" · ", parts);
    }

    /// <summary>«Proceso terminado a la fuerza (watchdog o externo) · la grabación siguió en una pieza nueva».
    /// El código −1 es un proceso matado; cualquier otro, FFmpeg saliendo por su cuenta.</summary>
    private static string Interrupted(JsonElement j)
    {
        int code = Int(j, "ExitCode");
        var how = code == -1
            ? Localizer.T("Audit_Interrupted_Killed")
            : Localizer.F("Audit_Interrupted_Exited", code);
        return $"{how} · {Localizer.T("Audit_Interrupted_Resumed")}";
    }

    /// <summary>«06-09-2026_TEST_9.mp4 · 2,7 GB · sin índice, no se puede reproducir».</summary>
    private static string Unverified(JsonElement j)
    {
        var path = Str(j, "FilePath");
        var name = string.IsNullOrEmpty(path) ? "" : System.IO.Path.GetFileName(path!);
        long bytes = Long(j, "SizeBytes");
        var parts = new List<string>(3) { name };
        if (bytes > 0) parts.Add(Bytes(bytes));
        parts.Add(Localizer.T("Audit_Unverified_Detail"));
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    private static string Segment(JsonElement j)
    {
        var path = Str(j, "FilePath");
        var name = string.IsNullOrEmpty(path) ? "" : System.IO.Path.GetFileName(path!);
        long bytes = Long(j, "SizeBytes");
        return bytes > 0 ? $"{name} · {Bytes(bytes)}" : name;
    }

    /// <summary>Motivo del paro en palabras. Acepta el número o el nombre: <c>PayloadJson</c> guarda los
    /// enumerados como enteros, pero un cambio de serialización no debe romper la lectura.</summary>
    private static string ReasonText(string? reason) => reason switch
    {
        "1" or "Operator" => Localizer.T("Audit_Stop_Operator"),
        "2" or "Api" => Localizer.T("Audit_Stop_Api"),
        "3" or "ScheduledEnd" => Localizer.T("Audit_Stop_ScheduledEnd"),
        "4" or "ScheduledSkip" => Localizer.T("Audit_Stop_ScheduledSkip"),
        "5" or "DiskFull" => Localizer.T("Audit_Stop_DiskFull"),
        "6" or "Shutdown" => Localizer.T("Audit_Stop_Shutdown"),
        "7" or "Error" => Localizer.T("Audit_Stop_Error"),
        _ => Localizer.T("Audit_Stop_Unknown"),
    };

    // --- Ayudas ---

    /// <summary>
    /// Limpia el <c>ToString()</c> de un record: quita el nombre del tipo, las llaves y los campos que ya van en
    /// columnas propias de la tabla (momento, canal, ids). Lo que queda son los datos del evento.
    /// </summary>
    internal static string Tidy(string message)
    {
        int open = message.IndexOf('{');
        int close = message.LastIndexOf('}');
        if (open < 0 || close <= open) return message;

        var body = message.Substring(open + 1, close - open - 1);
        var kept = new List<string>();
        foreach (var raw in body.Split(','))
        {
            var field = raw.Trim();
            int eq = field.IndexOf('=');
            if (eq <= 0) continue;
            var name = field.Substring(0, eq).Trim();
            var value = field.Substring(eq + 1).Trim();
            if (value.Length == 0) continue;                       // campo nulo: no aporta
            if (name is "OccurredAt" or "ChannelId" or "SessionId") continue; // ya van en sus columnas
            kept.Add($"{name} {value}");
        }
        return kept.Count > 0 ? string.Join(" · ", kept) : message;
    }

    private static string? Str(JsonElement j, string name)
    {
        if (!j.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.ToString(),
        };
    }

    private static int Int(JsonElement j, string name)
        => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement j, string name)
        => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>Duración «hh:mm:ss» → «1 h 05 min» / «40 s», que es como se lee de un vistazo.</summary>
    private static string? Duration(JsonElement j, string name)
    {
        var raw = Str(j, name);
        if (raw is null || !TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var d)) return null;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} h {d.Minutes:00} min";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes} min {d.Seconds:00} s";
        return $"{(int)d.TotalSeconds} s";
    }

    /// <summary>Tamaño legible. Propio y no <c>StorageFormat</c> para no acoplar la auditoría a Infrastructure.</summary>
    private static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0
            ? $"{bytes} {units[0]}"
            : string.Create(CultureInfo.CurrentCulture, $"{v:0.#} {units[i]}");
    }
}
