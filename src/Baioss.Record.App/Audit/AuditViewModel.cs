using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Audit;
using Baioss.Record.Application.Persistence;
using Baioss.Record.App.Localization;
using Baioss.Record.Application.Localization;
using Baioss.Record.App.Recordings;

namespace Baioss.Record.App.Audit;

/// <summary>Filtro por tipo de suceso. ToString = Label (el ComboBox oscuro muestra el elemento cerrado por
/// ToString, no por DisplayMemberPath — ver <see cref="RangeOption"/>).</summary>
public sealed record AuditKindOption(string Label, AuditKind Kind)
{
    public override string ToString() => Label;
}

/// <summary>Qué mirar: todo, solo lo relativo a grabaciones, o solo lo que fue mal.</summary>
public enum AuditKind { All, Recordings, Problems }

/// <summary>Filtro por nivel mínimo. <c>null</c> = cualquiera.</summary>
public sealed record AuditSeverityOption(string Label, EventSeverity? Minimum)
{
    public override string ToString() => Label;
}

/// <summary>
/// ViewModel de la ventana «Registro de actividad»: lee la tabla de auditoría y la presenta EN PALABRAS
/// (quién grabó, cómo, por qué terminó, y lo que no llegó a grabarse), con filtros y exportación a CSV.
///
/// Solo lectura por diseño: una auditoría que se puede editar desde la propia aplicación no vale como
/// auditoría. Tampoco se borra desde aquí — de la poda se encarga el escritor, por antigüedad.
/// </summary>
public sealed partial class AuditViewModel : ObservableObject, IDisposable
{
    /// <summary>Sucesos que cuentan como «de grabación» para el filtro rápido.</summary>
    private static readonly HashSet<string> RecordingCategories = new(StringComparer.Ordinal)
    {
        "RecordingStarted", "RecordingStopped", "RecordingRenamed", "RecordingStartFailed", "ScheduledRecordingSkipped", "ScheduleChanged",
        "SegmentCompleted", "RecordingPaused", "RecordingResumed", "RecordingRecovered", "OrphanSessionsClosed",
        "RecordingInterrupted", "RecordingFileUnverified",
    };

    private readonly IEventLogRepository _events;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<Guid, string> _channelKeys;

    public ObservableCollection<AuditRow> Entries { get; } = new();
    public ObservableCollection<RangeOption> Ranges { get; }
    public ObservableCollection<ChannelOption> Channels { get; }
    public ObservableCollection<AuditKindOption> Kinds { get; }
    public ObservableCollection<AuditSeverityOption> Levels { get; }

    [ObservableProperty] private RangeOption _selectedRange;
    [ObservableProperty] private ChannelOption _selectedChannel;
    [ObservableProperty] private AuditKindOption _selectedKind;
    [ObservableProperty] private AuditSeverityOption _selectedLevel;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty;

    public AuditViewModel(IEventLogRepository events, IClock clock, IReadOnlyDictionary<Guid, string> channelKeys)
    {
        _events = events;
        _clock = clock;
        _channelKeys = channelKeys;

        Ranges = new ObservableCollection<RangeOption>();
        Channels = new ObservableCollection<ChannelOption>();
        Kinds = new ObservableCollection<AuditKindOption>();
        Levels = new ObservableCollection<AuditSeverityOption>();
        BuildOptions();

        _selectedRange = Ranges[0];     // 7 días: la auditoría se consulta por lo recién ocurrido
        _selectedChannel = Channels[0];
        _selectedKind = Kinds[0];
        _selectedLevel = Levels[0];

        // Las etiquetas de los desplegables y el texto de cada fila se componen AQUÍ, no con enlaces {loc:T},
        // así que un cambio de idioma no los toca: hay que rehacerlos. Sin esto la ventana quedaba a medias
        // (cabeceras en un idioma, filtros y filas en el otro).
        Localizer.LanguageChanged += OnLanguageChanged;

        _ = LoadAsync();
    }

    /// <summary>Rehace las etiquetas de los filtros conservando lo elegido, y recarga las filas (el texto de
    /// cada suceso se traduce al componerlo).</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _reselecting = true;
            try
            {
                int days = SelectedRange?.Days ?? 7;
                Guid? channel = SelectedChannel?.ChannelId;
                AuditKind kind = SelectedKind?.Kind ?? AuditKind.All;
                EventSeverity? level = SelectedLevel?.Minimum;

                BuildOptions();

                SelectedRange = Ranges.FirstOrDefault(r => r.Days == days) ?? Ranges[0];
                SelectedChannel = Channels.FirstOrDefault(c => c.ChannelId == channel) ?? Channels[0];
                SelectedKind = Kinds.FirstOrDefault(k => k.Kind == kind) ?? Kinds[0];
                SelectedLevel = Levels.FirstOrDefault(l => l.Minimum == level) ?? Levels[0];
            }
            finally { _reselecting = false; }
            _ = LoadAsync();
        });

    /// <summary>Verdadero mientras se re-seleccionan los filtros tras cambiar de idioma: evita que cada
    /// asignación dispare su propia recarga (serían cuatro seguidas).</summary>
    private bool _reselecting;

    /// <summary>Suelta la suscripción al idioma. La llama la ventana al cerrarse: el <see cref="Localizer"/> es
    /// estático y viviría más que la ventana, dejando el ViewModel retenido en cada apertura.</summary>
    public void Dispose() => Localizer.LanguageChanged -= OnLanguageChanged;

    /// <summary>(Re)construye las etiquetas de los cuatro filtros en el idioma vigente.</summary>
    private void BuildOptions()
    {
        Ranges.Clear();
        Ranges.Add(new RangeOption(Loc.T("Rec_Range_7"), 7));
        Ranges.Add(new RangeOption(Loc.T("Rec_Range_30"), 30));
        Ranges.Add(new RangeOption(Loc.T("Rec_Range_90"), 90));

        Channels.Clear();
        Channels.Add(new ChannelOption(Loc.T("Rec_Filter_AllChannels"), null));
        foreach (var kv in _channelKeys.OrderBy(k => k.Value, StringComparer.Ordinal))
            Channels.Add(new ChannelOption(Loc.F("Rec_Filter_Channel", kv.Value), kv.Key));

        Kinds.Clear();
        Kinds.Add(new AuditKindOption(Loc.T("Audit_Filter_AllTypes"), AuditKind.All));
        Kinds.Add(new AuditKindOption(Loc.T("Audit_Filter_OnlyRecordings"), AuditKind.Recordings));
        Kinds.Add(new AuditKindOption(Loc.T("Audit_Filter_OnlyProblems"), AuditKind.Problems));

        Levels.Clear();
        Levels.Add(new AuditSeverityOption(Loc.T("Audit_Filter_AllLevels"), null));
        Levels.Add(new AuditSeverityOption(Loc.T("Audit_Sev_Warning"), EventSeverity.Warning));
        Levels.Add(new AuditSeverityOption(Loc.T("Audit_Sev_Error"), EventSeverity.Error));
    }

    partial void OnSelectedRangeChanged(RangeOption value) { if (!_reselecting) _ = LoadAsync(); }
    partial void OnSelectedChannelChanged(ChannelOption value) { if (!_reselecting) _ = LoadAsync(); }
    partial void OnSelectedKindChanged(AuditKindOption value) { if (!_reselecting) _ = LoadAsync(); }
    partial void OnSelectedLevelChanged(AuditSeverityOption value) { if (!_reselecting) _ = LoadAsync(); }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var to = _clock.UtcNow;
            var from = to - TimeSpan.FromDays(SelectedRange?.Days ?? 7);
            // Se piden más entradas de las que se van a mostrar porque los filtros de tipo y nivel se aplican
            // aquí: pedir justo el tope dejaría fuera entradas que sí encajan. El tope duro evita que una
            // semana con mucha actividad cargue decenas de miles de filas en la ventana.
            bool filtering = SelectedKind?.Kind is not AuditKind.All || SelectedLevel?.Minimum is not null;
            var list = await _events.QueryAsync(SelectedChannel?.ChannelId, from, to, filtering ? 20_000 : 2_000);

            Entries.Clear();
            foreach (var e in list.Where(Matches).Take(2_000)) Entries.Add(ToRow(e));

            IsEmpty = Entries.Count == 0;
            Summary = Loc.F(Entries.Count == 1 ? "Audit_Summary_One" : "Audit_Summary_Many", Entries.Count);
        }
        catch (Exception ex)
        {
            Summary = Loc.F("Audit_Msg_LoadFailed", ex.Message);
            IsEmpty = Entries.Count == 0;
        }
        finally { Busy = false; }
    }

    private bool Matches(EventLogEntry e)
    {
        if (SelectedLevel?.Minimum is { } min && e.Severity < min) return false;
        return SelectedKind?.Kind switch
        {
            AuditKind.Recordings => RecordingCategories.Contains(e.Category),
            AuditKind.Problems => e.Severity >= EventSeverity.Warning,
            _ => true,
        };
    }

    private AuditRow ToRow(EventLogEntry e) => new(
        e.Timestamp,
        e.Severity,
        SeverityText(e.Severity),
        AuditText.Category(e.Category),
        e.ChannelId is { } id && _channelKeys.TryGetValue(id, out var k) ? k : "—",
        string.IsNullOrWhiteSpace(e.Operator) ? "—" : e.Operator!,
        AuditText.Detail(e.Category, e.PayloadJson, e.Message));

    private static string SeverityText(EventSeverity s) => s switch
    {
        EventSeverity.Warning => Loc.T("Audit_Sev_Warning"),
        EventSeverity.Error => Loc.T("Audit_Sev_Error"),
        EventSeverity.Critical => Loc.T("Audit_Sev_Critical"),
        _ => Loc.T("Audit_Sev_Info"),
    };

    /// <summary>Guarda EN CSV lo que se está viendo (con los filtros aplicados), para entregarlo o archivarlo.</summary>
    [RelayCommand]
    private void Export()
    {
        if (Entries.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Audit_Dlg_ExportTitle"),
            Filter = Loc.T("Audit_Dlg_CsvFilter"),
            FileName = $"actividad_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = Entries
                .Select(r => new AuditLine(r.When, r.SeverityText, r.EventText, r.ChannelKey, r.Operator, r.Detail))
                .ToList();
            File.WriteAllText(dialog.FileName, AuditExporter.ToCsv(lines, Loc.T), new System.Text.UTF8Encoding(false));
            Summary = Loc.F("Audit_Msg_Exported", lines.Count, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { Summary = Loc.F("Audit_Msg_ExportFailed", ex.Message); }
    }
}
