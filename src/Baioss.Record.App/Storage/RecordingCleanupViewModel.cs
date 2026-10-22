using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;
using Baioss.Record.App.Localization;

namespace Baioss.Record.App.Storage;

/// <summary>
/// ViewModel de la ventana «Borrar grabaciones» (se abre desde «Almacenamiento»): lista TODAS las grabaciones por
/// fecha, con un rango opcional (desde / hasta), y las borra una a una, las marcadas o todas las del rango. Cada
/// borrado se confirma antes; el borrado en sí (archivos del disco + registro, y qué no se toca) es del
/// <see cref="IRecordingDeletionService"/>. Las grabaciones en curso y las protegidas se ven, pero no se pueden marcar.
/// </summary>
public sealed partial class RecordingCleanupViewModel : ObservableObject, IDisposable
{
    /// <summary>Tope de filas: una instalación 24/7 de años puede tener decenas de miles de grabaciones. Se cargan las
    /// más recientes del rango y el resumen avisa de que hay que acotarlo para ver el resto.</summary>
    public const int MaxRows = 20_000;

    private readonly IRecordingSessionRepository _sessions;
    private readonly IRecordingDeletionService _deletion;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<Guid, string> _channelKeys;
    private readonly Func<string, string, bool> _confirm;

    public ObservableCollection<RecordingCleanupRow> Rows { get; } = new();

    /// <summary>Primer día del rango (incluido); null = sin límite.</summary>
    [ObservableProperty] private DateTime? _fromDate;
    /// <summary>Último día del rango (incluido); null = hasta hoy.</summary>
    [ObservableProperty] private DateTime? _toDate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(DeleteRangeCommand), nameof(DeleteOneCommand))]
    private bool _busy;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _statusMessage = "";
    /// <summary>El mensaje es un aviso (algo no se pudo borrar): la ventana lo pinta en rojo.</summary>
    [ObservableProperty] private bool _statusIsError;

    /// <summary>Casilla «todas» de la cabecera: true/false si están todas o ninguna, null si solo algunas.</summary>
    [ObservableProperty] private bool? _allSelected = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteSelectedText))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private int _selectedCount;

    /// <summary>Filas que se pueden borrar (ni en curso ni protegidas).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteRangeText))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRangeCommand))]
    private int _deletableCount;

    public string DeleteSelectedText => Loc.F("Cln_DeleteSelected", SelectedCount);
    public string DeleteRangeText => Loc.F("Cln_DeleteRange", DeletableCount);

    /// <summary><paramref name="confirm"/>: muestra la pregunta (texto, título) y devuelve true si el operador acepta.</summary>
    public RecordingCleanupViewModel(IRecordingSessionRepository sessions, IRecordingDeletionService deletion, IClock clock,
        IReadOnlyDictionary<Guid, string> channelKeys, Func<string, string, bool> confirm)
    {
        _sessions = sessions;
        _deletion = deletion;
        _clock = clock;
        _channelKeys = channelKeys;
        _confirm = confirm;
        // Los textos de cada fila y del resumen se componen aquí: un cambio de idioma obliga a recargar.
        Localizer.LanguageChanged += OnLanguageChanged;
        RequestLoad();
    }

    /// <summary>La carga en curso (o la última). La ventana no la necesita; las pruebas la esperan.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    private bool _reloadRequested;
    private bool _clearingDates;
    private bool _syncingSelection;

    private void OnLanguageChanged(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(DeleteSelectedText));
            OnPropertyChanged(nameof(DeleteRangeText));
            RequestLoad();
        });

    /// <summary>Suelta la suscripción al idioma (estática: viviría más que la ventana). La llama la ventana al cerrarse.</summary>
    public void Dispose() => Localizer.LanguageChanged -= OnLanguageChanged;

    partial void OnFromDateChanged(DateTime? value) { if (!_clearingDates) RequestLoad(); }
    partial void OnToDateChanged(DateTime? value) { if (!_clearingDates) RequestLoad(); }

    [RelayCommand]
    private Task Refresh() => RequestLoad();

    /// <summary>Quita el rango: vuelven a verse todas las grabaciones.</summary>
    [RelayCommand]
    private Task AllDates()
    {
        _clearingDates = true;
        try { FromDate = null; ToDate = null; }
        finally { _clearingDates = false; }
        return RequestLoad();
    }

    /// <summary>Recarga la lista. Si ya hay una carga o un borrado en marcha, la pide para cuando acabe (un cambio de
    /// fechas a mitad de una carga no se pierde).</summary>
    private Task RequestLoad()
    {
        if (Busy) { _reloadRequested = true; return Loading; }
        return Loading = LoadLoopAsync();
    }

    private async Task LoadLoopAsync()
    {
        Busy = true;
        try
        {
            do { _reloadRequested = false; await LoadOnceAsync(); }
            while (_reloadRequested);
        }
        finally { Busy = false; }
    }

    private async Task LoadOnceAsync()
    {
        try
        {
            var (from, to) = Bounds();
            var list = await _sessions.GetHistoryAsync(null, from, to, skip: 0, take: MaxRows);
            var now = _deletion.RecordingNow();

            foreach (var old in Rows) old.PropertyChanged -= OnRowChanged;
            Rows.Clear();
            foreach (var s in list.OrderByDescending(s => s.StartedAt))
            {
                var row = new RecordingCleanupRow(s, _channelKeys.TryGetValue(s.ChannelId, out var k) ? k : "?",
                    RecordingDeletionRules.IsInProgress(s, now));
                row.PropertyChanged += OnRowChanged;
                Rows.Add(row);
            }

            DeletableCount = Rows.Count(r => r.CanDelete);
            UpdateSelection();
            IsEmpty = Rows.Count == 0;
            var summary = Loc.F(Rows.Count == 1 ? "Cln_Summary_One" : "Cln_Summary_Many",
                Rows.Count, StorageFormat.Bytes(list.Sum(s => s.TotalBytes)));
            Summary = list.Count >= MaxRows ? summary + " · " + Loc.F("Cln_Summary_Capped", MaxRows) : summary;
        }
        catch (Exception ex)
        {
            Summary = Loc.F("Cln_Msg_LoadFailed", ex.Message);
            IsEmpty = Rows.Count == 0;
        }
    }

    /// <summary>Del primer instante del día «desde» al último del día «hasta», días de la hora LOCAL (las fechas de la lista
    /// lo son), pasados a UTC: la BD guarda las fechas como un número que solo se compara bien entre fechas con el mismo
    /// desfase, y todas se guardan en UTC. Sin «desde», desde siempre; sin «hasta», hasta mucho después de hoy (que entren
    /// las que están en curso).</summary>
    private (DateTimeOffset From, DateTimeOffset To) Bounds()
    {
        DateTime? a = FromDate?.Date, b = ToDate?.Date;
        if (a is { } x && b is { } y && x > y) (a, b) = (y, x); // fechas al revés: se entiende el mismo rango
        var from = a is { } f ? new DateTimeOffset(DateTime.SpecifyKind(f, DateTimeKind.Unspecified)) : DateTimeOffset.UnixEpoch;
        var to = b is { } t
            ? new DateTimeOffset(DateTime.SpecifyKind(t.AddDays(1), DateTimeKind.Unspecified)).AddTicks(-1)
            : _clock.UtcNow.AddYears(1);
        return (from.ToUniversalTime(), to.ToUniversalTime());
    }

    /// <summary>El rango en palabras, para la confirmación de «Borrar todo el rango».</summary>
    private string RangeText()
    {
        DateTime? a = FromDate?.Date, b = ToDate?.Date;
        if (a is { } x && b is { } y && x > y) (a, b) = (y, x);
        static string D(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return (a, b) switch
        {
            (null, null) => Loc.T("Cln_Range_All"),
            ({ } f, null) => Loc.F("Cln_Range_Since", D(f)),
            (null, { } t) => Loc.F("Cln_Range_Until", D(t)),
            ({ } f, { } t) when f == t => Loc.F("Cln_Range_Day", D(f)),
            ({ } f, { } t) => Loc.F("Cln_Range_Between", D(f), D(t)),
        };
    }

    // ---------------------------------------------------------------------
    //  Selección
    // ---------------------------------------------------------------------

    partial void OnAllSelectedChanged(bool? value)
    {
        if (_syncingSelection || value is not bool all) return;
        _syncingSelection = true;
        try { foreach (var r in Rows) if (r.CanDelete) r.IsSelected = all; }
        finally { _syncingSelection = false; }
        UpdateSelection();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_syncingSelection && e.PropertyName == nameof(RecordingCleanupRow.IsSelected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        int selected = Rows.Count(r => r.IsSelected && r.CanDelete);
        SelectedCount = selected;
        _syncingSelection = true;
        try { AllSelected = selected == 0 ? false : selected == DeletableCount ? true : null; }
        finally { _syncingSelection = false; }
    }

    // ---------------------------------------------------------------------
    //  Borrado
    // ---------------------------------------------------------------------

    private enum Scope { One, Selected, Range }

    [RelayCommand(CanExecute = nameof(CanDeleteOne))]
    private Task DeleteOne(RecordingCleanupRow? row) => row is null ? Task.CompletedTask : DeleteAsync(new[] { row }, Scope.One);
    private bool CanDeleteOne(RecordingCleanupRow? row) => !Busy && row is { CanDelete: true };

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelected() => DeleteAsync(Rows.Where(r => r.IsSelected && r.CanDelete).ToList(), Scope.Selected);
    private bool CanDeleteSelected() => !Busy && SelectedCount > 0;

    /// <summary>Todas las de la lista, es decir, las del rango de fechas elegido. Se le pasan también las que no se pueden
    /// borrar para que la confirmación diga cuántas se quedan.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteRange))]
    private Task DeleteRange() => DeleteAsync(Rows.ToList(), Scope.Range);
    private bool CanDeleteRange() => !Busy && DeletableCount > 0;

    private async Task DeleteAsync(IReadOnlyList<RecordingCleanupRow> rows, Scope scope)
    {
        if (rows.Count == 0 || Busy) return;
        Busy = true;
        bool reload;
        try { reload = await ConfirmAndDeleteAsync(rows, scope); }
        catch (Exception ex) { SetStatus(Loc.F("Cln_Msg_DeleteFailed", ex.Message), error: true); reload = true; }
        finally { Busy = false; }
        // Tras borrar, la lista enseña lo que queda; y si cambiaron las fechas mientras tanto, se cargan las nuevas.
        if (reload || _reloadRequested) await RequestLoad();
    }

    /// <summary>Cuenta, pregunta y borra. Devuelve true si llegó a borrar (hay que recargar la lista).</summary>
    private async Task<bool> ConfirmAndDeleteAsync(IReadOnlyList<RecordingCleanupRow> rows, Scope scope)
    {
        SetStatus(Loc.T("Cln_Checking"), error: false);
        var sessions = rows.Select(r => r.Session).ToList();
        // Mira el disco archivo por archivo (y una carpeta de red caída tarda): fuera del hilo de la ventana.
        var plan = await Task.Run(() => _deletion.Plan(sessions));
        if (plan.Recordings == 0)
        {
            SetStatus(Loc.F("Cln_Msg_NothingToDelete", Excluded(plan.InProgress, plan.Protected)), error: true);
            return false;
        }
        SetStatus("", error: false);
        if (!_confirm(ConfirmationText(rows, plan, scope), Loc.T("Cln_Confirm_Title"))) return false;

        var ids = rows.Where(r => r.CanDelete).Select(r => r.Id).ToList();
        int total = ids.Count;
        SetStatus(Loc.F("Cln_Deleting", 0, total), error: false);
        // El avance se escribe en el acto desde el hilo del borrado (WPF lleva el cambio de la propiedad a la ventana):
        // con Progress<T> los avisos llegarían en cola y el último podría pisar el resultado.
        var progress = new InlineProgress(n => StatusMessage = Loc.F("Cln_Deleting", n, total));
        var result = await Task.Run(() => _deletion.DeleteAsync(ids, Environment.UserName, progress));
        SetStatus(ResultText(result), error: result.Issues.Count > 0 || result.DatabaseErrors > 0);
        return true;
    }

    private void SetStatus(string text, bool error)
    {
        StatusIsError = error;
        StatusMessage = text;
    }

    private string ConfirmationText(IReadOnlyList<RecordingCleanupRow> rows, RecordingDeletionPlan plan, Scope scope)
    {
        var lines = new List<string>(6);
        if (plan.Recordings == 1 && rows.FirstOrDefault(r => r.CanDelete) is { } only)
        {
            var name = only.FileName == "—" ? "" : $"«{only.FileName}» ";
            lines.Add(Loc.F("Cln_Confirm_One", name, only.ChannelKey, $"{only.DateText} {only.TimeText}"));
        }
        else lines.Add(scope == Scope.Range
            ? Loc.F("Cln_Confirm_Range", plan.Recordings, RangeText())
            : Loc.F("Cln_Confirm_Selected", plan.Recordings));

        if (plan.FilesFound > 0)
            lines.Add(Loc.F(plan.FilesFound == 1 ? "Cln_Confirm_Files_One" : "Cln_Confirm_Files_Many",
                plan.FilesFound, StorageFormat.Bytes(plan.BytesFound)));
        if (plan.FilesMissing > 0)
            lines.Add(Loc.Plural("Cln_Confirm_Missing_One", "Cln_Confirm_Missing_Many", plan.FilesMissing));
        if (plan.UnavailableLocations.Count > 0)
            lines.Add(Loc.F("Cln_Confirm_Unavailable", string.Join(", ", plan.UnavailableLocations)));
        if (plan.InProgress + plan.Protected > 0)
            lines.Add(Loc.F("Cln_Confirm_Excluded", Excluded(plan.InProgress, plan.Protected)));
        lines.Add(Loc.T("Cln_Confirm_Undo"));
        return string.Join("\n\n", lines);
    }

    private static string ResultText(RecordingDeletionResult r)
    {
        var parts = new List<string>(6);
        if (r.Recordings > 0) parts.Add(Loc.Plural("Cln_Done_One", "Cln_Done_Many", r.Recordings));
        if (r.FilesDeleted > 0)
            parts.Add(Loc.F(r.FilesDeleted == 1 ? "Cln_Done_Files_One" : "Cln_Done_Files_Many", r.FilesDeleted, StorageFormat.Bytes(r.BytesFreed)));
        if (r.FilesMissing > 0) parts.Add(Loc.Plural("Cln_Done_Missing_One", "Cln_Done_Missing_Many", r.FilesMissing));
        if (r.Issues.Count > 0) parts.Add(Loc.F("Cln_Done_NotDeleted", IssuesText(r.Issues)));
        if (r.DatabaseErrors > 0) parts.Add(Loc.Plural("Cln_Done_DbErrors_One", "Cln_Done_DbErrors_Many", r.DatabaseErrors));
        if (r.InProgress + r.Protected > 0) parts.Add(Loc.F("Cln_Done_Excluded", Excluded(r.InProgress, r.Protected)));
        return parts.Count == 0 ? Loc.T("Cln_Done_Nothing") : string.Join(" · ", parts);
    }

    /// <summary>Los tres primeros archivos que impidieron borrar su grabación, y cuántos más.</summary>
    private static string IssuesText(IReadOnlyList<RecordingFileIssue> issues)
    {
        var shown = issues.Take(3).Select(i => Loc.F(i.Problem switch
        {
            RecordingFileProblem.InUse => "Cln_Issue_InUse",
            RecordingFileProblem.AccessDenied => "Cln_Issue_Denied",
            _ => "Cln_Issue_Failed",
        }, i.FileName)).ToList();
        if (issues.Count > 3) shown.Add(Loc.F("Cln_Issue_More", issues.Count - 3));
        return string.Join("; ", shown);
    }

    /// <summary>«1 en curso y 3 protegidas o importantes».</summary>
    private static string Excluded(int inProgress, int protectedCount)
    {
        var parts = new List<string>(2);
        if (inProgress > 0) parts.Add(Loc.F("Cln_Excl_InProgress", inProgress));
        if (protectedCount > 0) parts.Add(Loc.Plural("Cln_Excl_Protected_One", "Cln_Excl_Protected_Many", protectedCount));
        return string.Join($" {Loc.T("Common_And")} ", parts);
    }

    /// <summary>Abre en el Explorador la carpeta de la grabación (si se conoce y existe).</summary>
    [RelayCommand]
    private void OpenFolder(RecordingCleanupRow? row)
    {
        if (row?.FolderPath is not { } folder || !Directory.Exists(folder)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch { /* best-effort: no bloquear la ventana si el shell falla */ }
    }

    /// <summary>Avance que se aplica en el hilo que lo informa, en orden.</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
