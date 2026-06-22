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
    [ObservableProperty] private string _date = "";       // yyyy-MM-dd (solo "Una vez")
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private string _time = "20:00";  // HH:mm
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private int _durationMinutes = 60;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private bool _segmentEnabled;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FormHint))] private int _segmentMinutes = 10;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>Aviso EN VIVO bajo el formulario: cruce de medianoche y segmentos ≥ duración (informativo).</summary>
    public string FormHint
    {
        get
        {
            var notes = new List<string>();
            if (TryParseTime(Time, out var tod) && DurationMinutes > 0)
            {
                var end = tod + TimeSpan.FromMinutes(DurationMinutes);
                if (end >= TimeSpan.FromDays(1))
                {
                    var over = end - TimeSpan.FromDays(end.Days);
                    notes.Add($"⏭ Termina al día siguiente (~{over.Hours:00}:{over.Minutes:00}).");
                }
            }
            if (SegmentEnabled && SegmentMinutes > 0 && DurationMinutes > 0 && SegmentMinutes >= DurationMinutes)
                notes.Add("⚠ Los segmentos son ≥ que la grabación: será un solo archivo.");
            return string.Join("    ", notes);
        }
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
        Date = _clock.UtcNow.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
    private async Task Add()
    {
        if (SelectedChannel is null) { StatusMessage = "Elige un canal."; return; }
        if (SelectedRecurrence is null) { StatusMessage = "Elige el tipo de repetición."; return; }
        if (!TryParseTime(Time, out var tod)) { StatusMessage = "Hora inválida: usa HH:mm (p. ej. 20:00)."; return; }
        if (DurationMinutes <= 0) { StatusMessage = "La duración debe ser mayor que 0 minutos."; return; }
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
            if (!DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            { StatusMessage = "Fecha inválida: usa yyyy-MM-dd (p. ej. 2026-06-25)."; return; }
            runAt = new DateTimeOffset(d.Year, d.Month, d.Day, tod.Hours, tod.Minutes, 0, offset);
            if (runAt <= now) { StatusMessage = "Esa fecha/hora ya pasó; elige una futura."; return; }
        }
        else
        {
            // (3) Primera ocurrencia FUTURA: si la hora de hoy ya pasó, empieza el próximo día válido
            // (evita que una tarea recién creada arranque un trozo de inmediato).
            runAt = ScheduleValidator.NextRecurringAnchor(kind, weekdays, tod, offset, now);
        }

        var job = new ScheduledJob
        {
            ChannelId = SelectedChannel.ChannelId,
            Action = ScheduledAction.StartRecording,
            Title = string.IsNullOrWhiteSpace(Title) ? "Grabación programada" : Title.Trim(),
            RunAt = runAt,
            Recurrence = kind,
            Weekdays = weekdays,
            Duration = TimeSpan.FromMinutes(DurationMinutes),
            SegmentMinutes = SegmentEnabled && SegmentMinutes > 0 ? SegmentMinutes : null,
            Enabled = true,
        };

        // (1) La duración no puede alcanzar la siguiente ocurrencia (si no, esa se perdería en silencio).
        if (!ScheduleValidator.DurationFitsInterval(job))
        {
            StatusMessage = $"La duración ({DurationMinutes} min) alcanza la siguiente ocurrencia ({DescribeInterval(ScheduleValidator.RecurrenceInterval(job))}). Redúcela o cambia la repetición.";
            return;
        }

        var existing = await _scheduler.GetAllAsync();

        // Nombre ÚNICO: no se permite guardar una tarea con un título que ya existe (los archivos se
        // nombran por título; dos iguales se confundirían). Sin distinción de mayúsculas.
        if (existing.Any(e => string.Equals(e.Title?.Trim(), job.Title, StringComparison.OrdinalIgnoreCase)))
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

        await _scheduler.ScheduleAsync(job);

        // Avisos suaves (no bloquean la creación).
        var notes = new List<string>();
        if (SegmentEnabled && SegmentMinutes >= DurationMinutes) notes.Add("los segmentos son ≥ que la grabación (un solo archivo)");
        if (ScheduleValidator.SpansToNextDay(job)) notes.Add($"termina al día siguiente a las {(job.RunAt + job.Duration!.Value).ToLocalTime():HH:mm}");
        StatusMessage = $"Programada «{job.Title}» en Canal {SelectedChannel.Key}."
                        + (notes.Count > 0 ? "  Aviso: " + string.Join("; ", notes) + "." : "");
        await RefreshAsync();
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

    private static bool TryParseTime(string s, out TimeSpan tod)
    {
        tod = default;
        return TimeSpan.TryParseExact(s?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out tod)
            && tod >= TimeSpan.Zero && tod < TimeSpan.FromDays(1);
    }
}
