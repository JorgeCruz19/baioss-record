using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Scheduling;

namespace Baioss.Record.App.Scheduling;

/// <summary>Canal elegible para una grabación programada.</summary>
public sealed class ScheduleChannelOption
{
    public required string Key { get; init; }
    public required Guid ChannelId { get; init; }
    public override string ToString() => $"Canal {Key}";
}

/// <summary>Opción de repetición para el combo (envuelve el enum con etiqueta legible).</summary>
public sealed class RecurrenceOption
{
    public required RecurrenceKind Kind { get; init; }
    public required string Label { get; init; }
    public override string ToString() => Label;
}

/// <summary>Fila de la lista de programaciones (un <see cref="ScheduledJob"/> presentado para la UI).</summary>
public sealed partial class ScheduledJobRow : ObservableObject
{
    public required ScheduledJob Job { get; init; }
    public required string ChannelKey { get; init; }
    public required string WhenText { get; init; }
    public required string NextRunText { get; init; }
    public required bool Enabled { get; init; }

    public string Title => Job.Title;
    public string EnabledText => Enabled ? "Pausar" : "Reanudar";
}

/// <summary>Una grabación PROGRAMADA que ocurre HOY (con su estado según la hora actual).</summary>
public sealed class TodayRecordingRow
{
    public required string ChannelKey { get; init; }
    public required string TimeText { get; init; }   // "20:00–21:00"
    public required string Title { get; init; }
    public required string Status { get; init; }     // Programada / En curso / Grabada / Saltada
    public required Brush StatusBrush { get; init; }
}

/// <summary>
/// ViewModel del gestor de programación: lista las grabaciones programadas y permite crear, pausar y
/// borrar. Cada programación es un <see cref="ScheduledAction.StartRecording"/> con duración (auto-stop).
/// La grabación usa el perfil activo del canal en el momento de disparar.
/// </summary>
public sealed partial class ScheduleViewModel : ObservableObject
{
    private readonly ISchedulerService _scheduler;
    private readonly IClock _clock;

    public ObservableCollection<ScheduledJobRow> Jobs { get; } = new();

    /// <summary>Grabaciones programadas que ocurren HOY (sección «HOY» de la ventana).</summary>
    public ObservableCollection<TodayRecordingRow> Today { get; } = new();
    [ObservableProperty] private bool _noTodayRecordings = true;

    public IReadOnlyList<ScheduleChannelOption> Channels { get; }
    public IReadOnlyList<RecurrenceOption> Recurrences { get; } = new[]
    {
        new RecurrenceOption { Kind = RecurrenceKind.Once,   Label = "Una vez" },
        new RecurrenceOption { Kind = RecurrenceKind.Daily,  Label = "Cada día" },
        new RecurrenceOption { Kind = RecurrenceKind.Weekly, Label = "Días de la semana" },
    };

    [ObservableProperty] private ScheduleChannelOption? _selectedChannel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnce))]
    [NotifyPropertyChangedFor(nameof(IsWeekly))]
    [NotifyPropertyChangedFor(nameof(FormHint))]
    private RecurrenceOption? _selectedRecurrence;

    [ObservableProperty] private string _title = "Grabación programada";

    /// <summary>Fecha de la grabación (solo «Una vez»); se elige con el DatePicker nativo.</summary>
    [ObservableProperty] private DateTime? _selectedDate;

    // Hora de INICIO y de FIN con segundos (hh:mm:ss), elegidas con selectores nativos (ComboBox).
    // La duración (auto-stop) se deriva de fin − inicio; si fin ≤ inicio se asume cruce de medianoche.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private string _startHour = "20";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private string _startMinute = "00";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private string _startSecond = "00";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private string _endHour = "21";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private string _endMinute = "00";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private string _endSecond = "00";

    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private bool _segmentEnabled;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private int _segmentMinutes = 10;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>Id de la tarea en edición (null = creando una nueva). Cambia el botón y el título del formulario.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    private Guid? _editingJobId;

    public bool IsEditing => EditingJobId is not null;
    public string SaveButtonText => IsEditing ? "💾 Guardar cambios" : "＋ Programar";
    public string FormTitle => IsEditing ? "EDITAR PROGRAMACIÓN" : "NUEVA PROGRAMACIÓN";

    /// <summary>Opciones «00»..«23» (horas) y «00»..«59» (minutos/segundos) para los selectores hh:mm:ss.</summary>
    public IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToArray();
    public IReadOnlyList<string> MinutesSeconds { get; } = Enumerable.Range(0, 60).Select(i => i.ToString("00")).ToArray();

    /// <summary>Aviso EN VIVO bajo el formulario: duración, cruce de medianoche y segmentos ≥ duración.</summary>
    public string FormHint
    {
        get
        {
            var notes = new List<string>();
            var start = StartTimeOfDay;
            var end = EndTimeOfDay;
            var dur = DurationFromStartEnd(start, end);
            if (dur > TimeSpan.Zero) notes.Add($"⏱ Duración: {DescribeDuration(dur)}.");
            if (end < start) notes.Add("⏭ La hora de fin es anterior a la de inicio: termina al día siguiente.");
            if (SegmentEnabled && SegmentMinutes > 0 && dur > TimeSpan.Zero && TimeSpan.FromMinutes(SegmentMinutes) >= dur)
                notes.Add("⚠ Los segmentos son ≥ que la grabación: será un solo archivo.");
            return string.Join("     ", notes);
        }
    }

    private TimeSpan StartTimeOfDay => Tod(StartHour, StartMinute, StartSecond);
    private TimeSpan EndTimeOfDay => Tod(EndHour, EndMinute, EndSecond);
    private static TimeSpan Tod(string h, string m, string s) => new(Parse(h), Parse(m), Parse(s));
    private static int Parse(string s) => int.TryParse(s, out var v) ? v : 0;

    /// <summary>Duración inicio→fin; si fin ≤ inicio se asume cruce de medianoche (fin del día siguiente).</summary>
    private static TimeSpan DurationFromStartEnd(TimeSpan start, TimeSpan end)
        => end > start ? end - start
         : end < start ? end + TimeSpan.FromDays(1) - start
         : TimeSpan.Zero;

    private static string DescribeDuration(TimeSpan d)
    {
        var parts = new List<string>();
        if (d.Hours > 0) parts.Add($"{d.Hours} h");
        if (d.Minutes > 0) parts.Add($"{d.Minutes} min");
        if (d.Seconds > 0) parts.Add($"{d.Seconds} s");
        return parts.Count > 0 ? string.Join(" ", parts) : "0 s";
    }

    [ObservableProperty] private bool _mon;
    [ObservableProperty] private bool _tue;
    [ObservableProperty] private bool _wed;
    [ObservableProperty] private bool _thu;
    [ObservableProperty] private bool _fri;
    [ObservableProperty] private bool _sat;
    [ObservableProperty] private bool _sun;

    public bool IsOnce => SelectedRecurrence?.Kind == RecurrenceKind.Once;
    public bool IsWeekly => SelectedRecurrence?.Kind == RecurrenceKind.Weekly;

    public ScheduleViewModel(ISchedulerService scheduler, IReadOnlyList<ChannelViewModel> channels, IClock clock)
    {
        _scheduler = scheduler;
        _clock = clock;
        Channels = channels.Select(c => new ScheduleChannelOption { Key = c.Key, ChannelId = c.ChannelId }).ToList();
        SelectedChannel = Channels.FirstOrDefault();
        SelectedRecurrence = Recurrences[0];
        SelectedDate = _clock.UtcNow.ToLocalTime().Date;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        var now = _clock.UtcNow;
        var all = await _scheduler.GetAllAsync();
        Jobs.Clear();
        foreach (var j in all.OrderBy(j => ScheduleEvaluator.NextSlotAfter(j, now) ?? DateTimeOffset.MaxValue))
        {
            var next = ScheduleEvaluator.NextSlotAfter(j, now);
            Jobs.Add(new ScheduledJobRow
            {
                Job = j,
                ChannelKey = Channels.FirstOrDefault(c => c.ChannelId == j.ChannelId)?.Key ?? "—",
                WhenText = DescribeWhen(j),
                NextRunText = next is { } n
                    ? n.ToLocalTime().ToString("ddd dd/MM HH:mm", new CultureInfo("es-ES"))
                    : (j.Recurrence == RecurrenceKind.Once ? "ya ejecutada" : "—"),
                Enabled = j.Enabled,
            });
        }
        BuildToday(all, now);
        StatusMessage = Jobs.Count == 0
            ? "No hay grabaciones programadas. Crea una abajo."
            : $"{Jobs.Count} programación(es). El scheduler corre mientras la app esté abierta.";
    }

    /// <summary>Lista las grabaciones (habilitadas) que ocurren HOY, ordenadas por hora, con su estado.</summary>
    private void BuildToday(IReadOnlyList<ScheduledJob> all, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        var rows = new List<(DateTimeOffset Slot, TodayRecordingRow Row)>();

        foreach (var j in all)
        {
            if (!j.Enabled) continue;                                    // pausada → hoy no graba
            if (ScheduleEvaluator.OccurrenceOnDate(j, today, requireAfterAnchor: false) is not { } slot) continue;
            var end = j.Duration is { } d ? slot + d : (DateTimeOffset?)null;

            var (status, brush) = TodayStatus(j, slot, end, now);
            string timeText = end is { } e
                ? $"{slot.ToLocalTime():HH:mm}–{e.ToLocalTime():HH:mm}"
                : slot.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

            rows.Add((slot, new TodayRecordingRow
            {
                ChannelKey = Channels.FirstOrDefault(c => c.ChannelId == j.ChannelId)?.Key ?? "—",
                TimeText = timeText,
                Title = j.Title,
                Status = status,
                StatusBrush = brush,
            }));
        }

        Today.Clear();
        foreach (var r in rows.OrderBy(r => r.Slot)) Today.Add(r.Row);
        NoTodayRecordings = Today.Count == 0;
    }

    private static (string Status, Brush Brush) TodayStatus(ScheduledJob job, DateTimeOffset slot, DateTimeOffset? end, DateTimeOffset now)
    {
        if (job.SkippedOccurrence is { } sk && sk == slot) return ("Saltada", SaltadaBrush);
        if (now < slot) return ("Programada", ProgramadaBrush);
        if (end is { } e && now >= e) return ("Grabada", GrabadaBrush);
        return ("En curso", EnCursoBrush); // entre el inicio y el fin (o sin duración, ya iniciada)
    }

    private static readonly Brush ProgramadaBrush = Frozen("#8A92A0");
    private static readonly Brush EnCursoBrush = Frozen("#E5484D");
    private static readonly Brush GrabadaBrush = Frozen("#30A46C");
    private static readonly Brush SaltadaBrush = Frozen("#F5A623");
    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (SelectedChannel is null) { StatusMessage = "Elige un canal."; return; }
        if (SelectedRecurrence is null) { StatusMessage = "Elige el tipo de repetición."; return; }

        // Hora de inicio/fin (hh:mm:ss). La duración (auto-stop) es fin − inicio: fin == inicio no es válido
        // y fin < inicio se interpreta como cruce de medianoche (la grabación termina al día siguiente).
        var start = StartTimeOfDay;
        var end = EndTimeOfDay;
        if (end == start) { StatusMessage = "La hora de fin debe ser distinta de la de inicio."; return; }
        var duration = DurationFromStartEnd(start, end);
        if (SegmentEnabled && SegmentMinutes <= 0) { StatusMessage = "Los minutos por segmento deben ser mayores que 0."; return; }

        var kind = SelectedRecurrence.Kind;
        var offset = DateTimeOffset.Now.Offset;
        var now = _clock.UtcNow;

        var weekdays = Weekdays.None;
        if (kind == RecurrenceKind.Weekly)
        {
            if (Mon) weekdays |= Weekdays.Monday;
            if (Tue) weekdays |= Weekdays.Tuesday;
            if (Wed) weekdays |= Weekdays.Wednesday;
            if (Thu) weekdays |= Weekdays.Thursday;
            if (Fri) weekdays |= Weekdays.Friday;
            if (Sat) weekdays |= Weekdays.Saturday;
            if (Sun) weekdays |= Weekdays.Sunday;
            if (weekdays == Weekdays.None) { StatusMessage = "Elige al menos un día de la semana."; return; }
        }

        DateTimeOffset runAt;
        if (kind == RecurrenceKind.Once)
        {
            if (SelectedDate is not { } d) { StatusMessage = "Elige una fecha."; return; }
            runAt = new DateTimeOffset(d.Year, d.Month, d.Day, start.Hours, start.Minutes, start.Seconds, offset);
            if (runAt <= now) { StatusMessage = "Esa fecha/hora ya pasó; elige una futura."; return; }
        }
        else
        {
            // (3) Primera ocurrencia FUTURA: si la hora de hoy ya pasó, empieza el próximo día válido
            // (evita que una tarea recién creada arranque un trozo de inmediato).
            runAt = ScheduleValidator.NextRecurringAnchor(kind, weekdays, start, offset, now);
        }

        var job = new ScheduledJob
        {
            Id = EditingJobId ?? Guid.NewGuid(),   // al editar conserva el mismo Id (es la misma tarea)
            ChannelId = SelectedChannel.ChannelId,
            Action = ScheduledAction.StartRecording,
            Title = string.IsNullOrWhiteSpace(Title) ? "Grabación programada" : Title.Trim(),
            RunAt = runAt,
            Recurrence = kind,
            Weekdays = weekdays,
            Duration = duration,
            SegmentMinutes = SegmentEnabled && SegmentMinutes > 0 ? SegmentMinutes : null,
            Enabled = true,
        };

        // (1) La duración no puede alcanzar la siguiente ocurrencia (si no, esa se perdería en silencio).
        if (!ScheduleValidator.DurationFitsInterval(job))
        {
            StatusMessage = $"La duración ({DescribeDuration(duration)}) alcanza la siguiente ocurrencia ({DescribeInterval(ScheduleValidator.RecurrenceInterval(job))}). Redúcela o cambia la repetición.";
            return;
        }

        var existing = await _scheduler.GetAllAsync();

        // Al editar, conserva el estado de pausa de la tarea original (editar no la reactiva sola).
        if (EditingJobId is { } eid) job.Enabled = existing.FirstOrDefault(e => e.Id == eid)?.Enabled ?? true;

        // Nombre ÚNICO: no se permite guardar una tarea con un título que ya existe (los archivos se
        // nombran por título; dos iguales se confundirían). Sin mayúsculas; al editar no choca consigo misma.
        if (existing.Any(e => e.Id != job.Id && string.Equals(e.Title?.Trim(), job.Title, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"Ya existe una grabación programada llamada «{job.Title}». Elige otro nombre.";
            return;
        }

        // (2) No solapar con otra tarea del mismo canal (doble reserva).
        var clash = existing.FirstOrDefault(e => e.Enabled && ScheduleValidator.Overlaps(job, e, now));
        if (clash is not null)
        {
            StatusMessage = $"Choca con «{clash.Title}» en el Canal {SelectedChannel.Key}. Ajusta la hora o la duración.";
            return;
        }

        bool wasEditing = IsEditing;
        if (wasEditing) await _scheduler.UpdateAsync(job);
        else await _scheduler.ScheduleAsync(job);

        // Avisos suaves (no bloquean el guardado).
        var notes = new List<string>();
        if (SegmentEnabled && TimeSpan.FromMinutes(SegmentMinutes) >= duration) notes.Add("los segmentos son ≥ que la grabación (un solo archivo)");
        if (ScheduleValidator.SpansToNextDay(job)) notes.Add($"termina al día siguiente a las {(job.RunAt + job.Duration!.Value).ToLocalTime():HH:mm}");
        StatusMessage = $"{(wasEditing ? "Actualizada" : "Programada")} «{job.Title}» en Canal {SelectedChannel.Key}."
                        + (notes.Count > 0 ? "  Aviso: " + string.Join("; ", notes) + "." : "");
        if (wasEditing) { EditingJobId = null; ResetForm(); }   // sale del modo edición y limpia el formulario
        await RefreshAsync();
    }

    /// <summary>Carga una tarea existente en el formulario para editarla (botón «Editar» de la lista).</summary>
    [RelayCommand]
    private void Edit(ScheduledJobRow? row)
    {
        if (row is null) return;
        var j = row.Job;
        EditingJobId = j.Id;
        SelectedChannel = Channels.FirstOrDefault(c => c.ChannelId == j.ChannelId) ?? SelectedChannel;
        SelectedRecurrence = Recurrences.FirstOrDefault(r => r.Kind == j.Recurrence) ?? Recurrences[0];
        Title = j.Title;

        var local = j.RunAt.ToLocalTime();
        SelectedDate = local.Date;
        var s = local.TimeOfDay;
        StartHour = s.Hours.ToString("00"); StartMinute = s.Minutes.ToString("00"); StartSecond = s.Seconds.ToString("00");
        // Fin = inicio + duración, normalizado al día (si cruza medianoche, queda la hora del día siguiente).
        var e = TimeSpan.FromTicks((s + (j.Duration ?? TimeSpan.Zero)).Ticks % TimeSpan.FromDays(1).Ticks);
        EndHour = e.Hours.ToString("00"); EndMinute = e.Minutes.ToString("00"); EndSecond = e.Seconds.ToString("00");

        Mon = j.Weekdays.HasFlag(Weekdays.Monday);
        Tue = j.Weekdays.HasFlag(Weekdays.Tuesday);
        Wed = j.Weekdays.HasFlag(Weekdays.Wednesday);
        Thu = j.Weekdays.HasFlag(Weekdays.Thursday);
        Fri = j.Weekdays.HasFlag(Weekdays.Friday);
        Sat = j.Weekdays.HasFlag(Weekdays.Saturday);
        Sun = j.Weekdays.HasFlag(Weekdays.Sunday);

        SegmentEnabled = j.SegmentMinutes is > 0;
        SegmentMinutes = j.SegmentMinutes is { } m && m > 0 ? m : 10;

        StatusMessage = $"Editando «{j.Title}». Cambia lo necesario y pulsa «Guardar cambios».";
    }

    /// <summary>Cancela la edición en curso y limpia el formulario.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        EditingJobId = null;
        ResetForm();
        StatusMessage = "Edición cancelada.";
    }

    /// <summary>Devuelve el formulario a sus valores por defecto.</summary>
    private void ResetForm()
    {
        Title = "Grabación programada";
        SelectedChannel = Channels.FirstOrDefault();
        SelectedRecurrence = Recurrences[0];
        SelectedDate = _clock.UtcNow.ToLocalTime().Date;
        StartHour = "20"; StartMinute = "00"; StartSecond = "00";
        EndHour = "21"; EndMinute = "00"; EndSecond = "00";
        Mon = Tue = Wed = Thu = Fri = Sat = Sun = false;
        SegmentEnabled = false; SegmentMinutes = 10;
    }

    private static string DescribeInterval(TimeSpan? iv)
        => iv is not { } t ? "—"
           : (int)t.TotalDays == 1 ? "cada día"
           : t.TotalDays >= 1 ? $"cada {(int)t.TotalDays} días"
           : $"cada {(int)t.TotalHours} h";

    [RelayCommand]
    private async Task Delete(ScheduledJobRow? row)
    {
        if (row is null) return;
        await _scheduler.CancelAsync(row.Job.Id);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ToggleEnabled(ScheduledJobRow? row)
    {
        if (row is null) return;
        await _scheduler.SetEnabledAsync(row.Job.Id, !row.Job.Enabled);
        await RefreshAsync();
    }

    private static string DescribeWhen(ScheduledJob j)
    {
        string time = j.RunAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        string dur = j.Duration is { } d ? $" · {(int)d.TotalMinutes} min" : "";
        string seg = j.SegmentMinutes is { } sm && sm > 0 ? $" · segmentos {sm} min" : "";
        string head = j.Recurrence switch
        {
            RecurrenceKind.Once => $"Una vez · {j.RunAt.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} {time}",
            RecurrenceKind.Daily => $"Cada día · {time}",
            RecurrenceKind.Weekly => $"{DescribeDays(j.Weekdays)} · {time}",
            _ => time,
        };
        string spans = ScheduleValidator.SpansToNextDay(j)
            ? $" · termina {(j.RunAt + j.Duration!.Value).ToLocalTime():HH:mm} del día sig."
            : "";
        return head + dur + seg + spans;
    }

    private static string DescribeDays(Weekdays w)
    {
        if (w == Weekdays.EveryDay) return "Todos los días";
        var parts = new List<string>();
        if (w.HasFlag(Weekdays.Monday)) parts.Add("L");
        if (w.HasFlag(Weekdays.Tuesday)) parts.Add("M");
        if (w.HasFlag(Weekdays.Wednesday)) parts.Add("X");
        if (w.HasFlag(Weekdays.Thursday)) parts.Add("J");
        if (w.HasFlag(Weekdays.Friday)) parts.Add("V");
        if (w.HasFlag(Weekdays.Saturday)) parts.Add("S");
        if (w.HasFlag(Weekdays.Sunday)) parts.Add("D");
        return parts.Count > 0 ? string.Join(" ", parts) : "—";
    }

}
