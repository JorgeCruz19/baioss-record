using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.App.Recordings;

/// <summary>Rango temporal del historial (últimos N días). <c>ToString</c> = Label: el ComboBox oscuro
/// (plantilla propia) muestra el elemento cerrado por ToString, no por DisplayMemberPath.</summary>
public sealed record RangeOption(string Label, int Days)
{
    public override string ToString() => Label;
}

/// <summary>Filtro de canal («Todos» = null, o un canal concreto). ToString = Label (ver <see cref="RangeOption"/>).</summary>
public sealed record ChannelOption(string Label, Guid? ChannelId)
{
    public override string ToString() => Label;
}

/// <summary>
/// ViewModel de la ventana «Grabaciones»: lista el historial de grabaciones (canal, fecha, duración, tamaño
/// y protección) desde el repositorio, y permite MARCAR cada una como Protegida/Importante/Normal frente a la
/// limpieza automática (Fase 1: cualquier valor ≠ Normal la excluye de la retención). Solo lectura + marcado;
/// NO borra (el borrado es de la retención automática, opt-in). (Gestión de almacenamiento — Fase 4a.)
/// </summary>
public sealed partial class RecordingsViewModel : ObservableObject
{
    private readonly IRecordingSessionRepository _sessions;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<Guid, string> _channelKeys;
    private long _lastTotalBytes;

    public ObservableCollection<RecordingRow> Recordings { get; } = new();
    public ObservableCollection<RangeOption> Ranges { get; }
    public ObservableCollection<ChannelOption> Channels { get; }

    [ObservableProperty] private RangeOption _selectedRange;
    [ObservableProperty] private ChannelOption _selectedChannel;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty;

    public RecordingsViewModel(IRecordingSessionRepository sessions, IClock clock, IReadOnlyDictionary<Guid, string> channelKeys)
    {
        _sessions = sessions;
        _clock = clock;
        _channelKeys = channelKeys;

        Ranges = new ObservableCollection<RangeOption>
        {
            new("Últimos 7 días", 7),
            new("Últimos 30 días", 30),
            new("Últimos 90 días", 90),
            new("Último año", 365),
        };
        Channels = new ObservableCollection<ChannelOption> { new("Todos los canales", null) };
        foreach (var kv in channelKeys.OrderBy(k => k.Value, StringComparer.Ordinal))
            Channels.Add(new ChannelOption($"Canal {kv.Value}", kv.Key));

        _selectedRange = Ranges[1];   // 30 días por defecto
        _selectedChannel = Channels[0];
        _ = LoadAsync();
    }

    partial void OnSelectedRangeChanged(RangeOption value) => _ = LoadAsync();
    partial void OnSelectedChannelChanged(ChannelOption value) => _ = LoadAsync();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var to = _clock.UtcNow;
            var from = to - TimeSpan.FromDays(SelectedRange?.Days ?? 30);
            var list = await _sessions.GetHistoryAsync(SelectedChannel?.ChannelId, from, to, skip: 0, take: 1000);

            Recordings.Clear();
            foreach (var s in list.OrderByDescending(s => s.StartedAt)) Recordings.Add(ToRow(s));

            _lastTotalBytes = list.Sum(s => s.TotalBytes);
            UpdateSummary();
            IsEmpty = Recordings.Count == 0;
        }
        catch (Exception ex)
        {
            Summary = $"Error al cargar el historial: {ex.Message}";
            IsEmpty = Recordings.Count == 0;
        }
        finally { Busy = false; }
    }

    private RecordingRow ToRow(RecordingSession s)
    {
        var local = s.StartedAt.ToLocalTime();
        string key = _channelKeys.TryGetValue(s.ChannelId, out var k) ? k : "?";
        string? firstPath = s.Segments.Count > 0 && !string.IsNullOrEmpty(s.Segments[0].FilePath)
            ? s.Segments[0].FilePath
            : null;
        string? folder = firstPath is not null ? Path.GetDirectoryName(firstPath) : null;
        string fileName = firstPath is not null ? Path.GetFileName(firstPath) : "—";
        if (s.Segments.Count > 1) fileName += $"  (+{s.Segments.Count - 1})"; // grabación segmentada: indica cuántos más
        return new RecordingRow(
            s.Id, key, fileName,
            local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            local.ToString("HH:mm", CultureInfo.InvariantCulture),
            StorageFormat.Duration(s.Duration),
            StorageFormat.Bytes(s.TotalBytes),
            string.IsNullOrWhiteSpace(s.Operator) ? "—" : s.Operator!,
            string.IsNullOrEmpty(folder) ? null : folder,
            s.Protection);
    }

    private void UpdateSummary()
    {
        int prot = Recordings.Count(r => !r.IsNormal);
        Summary = $"{Recordings.Count} grabación(es) · {StorageFormat.Bytes(_lastTotalBytes)} · {prot} protegida(s)";
    }

    [RelayCommand] private Task Protect(RecordingRow? row) => SetProtectionAsync(row, RecordingProtection.Protected);
    [RelayCommand] private Task MarkImportant(RecordingRow? row) => SetProtectionAsync(row, RecordingProtection.Important);
    [RelayCommand] private Task Unprotect(RecordingRow? row) => SetProtectionAsync(row, RecordingProtection.None);

    /// <summary>Persiste el nuevo nivel de protección y refleja el cambio en la fila. Solo si cambia.</summary>
    private async Task SetProtectionAsync(RecordingRow? row, RecordingProtection level)
    {
        if (row is null || row.Protection == level) return;
        try
        {
            if (await _sessions.SetProtectionAsync(row.Id, level))
            {
                row.Protection = level;   // la fila es observable → el chip se actualiza al instante
                UpdateSummary();          // refresca el contador de protegidas
            }
            else Summary = "La grabación ya no existe (¿la borró la limpieza?). Actualiza la lista.";
        }
        catch (Exception ex) { Summary = $"No se pudo cambiar la protección: {ex.Message}"; }
    }

    /// <summary>Abre en el Explorador la carpeta que contiene la grabación (si se conoce y existe).</summary>
    [RelayCommand]
    private void OpenFolder(RecordingRow? row)
    {
        if (row?.FolderPath is not { } folder || !Directory.Exists(folder)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch { /* best-effort: no bloquear la UI si el shell falla */ }
    }
}
