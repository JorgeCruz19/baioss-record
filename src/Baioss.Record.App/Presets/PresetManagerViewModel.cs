using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Baioss.Record.Application.Presets;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.App.Presets;

/// <summary>Filtro del panel izquierdo: "Todos", "Favoritos" o una categoría concreta.</summary>
public sealed record CategoryFilter(string Label, PresetCategory? Category, bool FavoritesOnly = false);

/// <summary>
/// ViewModel del gestor de presets (3 paneles). Panel izquierdo: formatos/categorías.
/// Centro: presets filtrados por categoría + búsqueda. Derecho: detalle + línea de comandos
/// FFmpeg + acciones (favorito, nuevo, editar, duplicar, eliminar, importar, exportar, aplicar).
/// </summary>
public sealed partial class PresetManagerViewModel : ObservableObject, IDisposable
{
    private readonly IPresetStore _store;
    private readonly EventHandler _onStoreChanged;

    public IReadOnlyList<ChannelViewModel> Channels { get; }
    public ObservableCollection<CategoryFilter> Categories { get; }
    public ObservableCollection<EncodingPreset> Presets { get; } = new();

    [ObservableProperty] private CategoryFilter? _selectedCategory;
    [ObservableProperty] private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private EncodingPreset? _selectedPreset;

    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ApplyCommand))] private ChannelViewModel? _targetChannel;
    [ObservableProperty] private string _commandLine = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _statusMessage = "";

    public PresetManagerViewModel(IPresetStore store, IReadOnlyList<ChannelViewModel> channels)
    {
        _store = store;
        Channels = channels;
        TargetChannel = channels.FirstOrDefault();

        Categories = new ObservableCollection<CategoryFilter>(new[]
        {
            new CategoryFilter("Todos", null),
            new CategoryFilter("★ Favoritos", null, true),
            new CategoryFilter("MPEG-2", PresetCategory.Mpeg2),
            new CategoryFilter("H.264", PresetCategory.H264),
            new CategoryFilter("H.265 / HEVC", PresetCategory.H265),
            new CategoryFilter("DNxHD / DNxHR", PresetCategory.DnxHd),
            new CategoryFilter("ProRes", PresetCategory.ProRes),
            new CategoryFilter("XDCAM", PresetCategory.Xdcam),
            new CategoryFilter("MXF OP1A", PresetCategory.Mxf),
            new CategoryFilter("AVI", PresetCategory.Avi),
            new CategoryFilter("MKV", PresetCategory.Mkv),
            new CategoryFilter("Audio", PresetCategory.Audio),
            new CategoryFilter("Streaming", PresetCategory.Streaming),
            new CategoryFilter("Proxy", PresetCategory.Proxy),
            new CategoryFilter("Archive", PresetCategory.Archive),
        });
        SelectedCategory = Categories[0];

        // Handler guardado en un campo para poder DESuscribirse en Dispose: el store es singleton de larga
        // vida; antes la lambda anónima dejaba el VM (y su snapshot de canales) vivo para siempre, y todos los
        // VMs colgados seguían haciendo Refresh en el Dispatcher por cada cambio del store. (Auditoría #24.)
        _onStoreChanged = (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(Refresh);
        _store.Changed += _onStoreChanged;
        Refresh();
    }

    /// <summary>Desuscribe del store singleton para no retener el VM. La ventana lo llama al cerrarse. (#24)</summary>
    public void Dispose() => _store.Changed -= _onStoreChanged;

    partial void OnSelectedCategoryChanged(CategoryFilter? value) => Refresh();
    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSelectedPresetChanged(EncodingPreset? value)
    {
        CommandLine = value is null ? "" : FfmpegCommandPreview.Build(value.ToProfile());
        Detail = value is null ? "" : BuildDetail(value);
    }

    private static string BuildDetail(EncodingPreset p)
    {
        string res = p is { Width: { } w, Height: { } h } ? $"{w}×{h}" : "nativa";
        string fps = p.FrameRateNum > 0 ? (p.FrameRateNum / (double)p.FrameRateDen).ToString("0.###") : "fuente";
        string max = p.MaxBitrateMbps > 0 ? $" (máx {p.MaxBitrateMbps:0.#})" : "";
        string vbr = p.AudioOnly ? "—" : $"{p.VideoBitrateMbps:0.#} Mbps{max}";
        return string.Join('\n', new[]
        {
            $"Contenedor:    {p.Container}",
            $"Solo audio:    {(p.AudioOnly ? "sí" : "no")}",
            $"Códec video:   {(p.AudioOnly ? "—" : p.VideoCodec.ToString())}",
            $"Resolución:    {(p.AudioOnly ? "—" : res)}",
            $"FPS:           {(p.AudioOnly ? "—" : fps)}",
            $"Bitrate:       {vbr}",
            $"GOP:           {(p.AudioOnly ? "—" : p.GopSize.ToString())}",
            $"Pixel format:  {(p.AudioOnly ? "—" : p.PixelFormat.ToString())}",
            $"Escaneo:       {(p.AudioOnly ? "—" : p.ScanType.ToString())}",
            $"Control tasa:  {(p.AudioOnly ? "—" : p.RateControl.ToString())}",
            "— Audio —",
            $"Códec:         {p.AudioCodec}",
            $"Canales:       {p.AudioLayout} ({p.AudioChannels} ch)",
            $"Sample rate:   {p.AudioSampleRate} Hz",
            $"Bitrate audio: {p.AudioBitrateKbps} kbps",
        });
    }

    private void Refresh()
    {
        IEnumerable<EncodingPreset> query = _store.GetAll();

        if (SelectedCategory is { } f)
        {
            if (f.FavoritesOnly) query = query.Where(p => p.IsFavorite);
            else if (f.Category is { } c) query = query.Where(p => p.Category == c);
        }
        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(p =>
                p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        var keepId = SelectedPreset?.Id;
        Presets.Clear();
        foreach (var p in query.OrderBy(p => p.Category).ThenBy(p => p.Name)) Presets.Add(p);
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == keepId) ?? Presets.FirstOrDefault();
    }

    private bool HasSelection() => SelectedPreset is not null;
    private bool HasCustomSelection() => SelectedPreset is { IsBuiltIn: false };
    private bool CanApply() => SelectedPreset is not null && TargetChannel is { IsConfigurable: true, IsRecording: false };

    [RelayCommand]
    private void ToggleFavorite(EncodingPreset? preset)
    {
        if (preset is not null) _store.SetFavorite(preset.Id, !preset.IsFavorite);
    }

    [RelayCommand]
    private void New()
    {
        var preset = new EncodingPreset { Name = "Nuevo preset", Category = SelectedCategory?.Category ?? PresetCategory.H264 };
        if (ShowEditor(preset)) { _store.Save(preset); SelectAfterRefresh(preset.Id); }
    }

    [RelayCommand(CanExecute = nameof(HasCustomSelection))]
    private void Edit()
    {
        if (SelectedPreset is not { IsBuiltIn: false } original) return;
        var draft = original.CloneKeepId(); // edita un borrador; solo se guarda si confirma
        if (ShowEditor(draft)) { _store.Save(draft); SelectAfterRefresh(draft.Id); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        var copy = _store.Duplicate(SelectedPreset!.Id);
        SelectAfterRefresh(copy.Id);
    }

    [RelayCommand(CanExecute = nameof(HasCustomSelection))]
    private void Delete()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset) return;
        if (MessageBox.Show($"¿Eliminar el preset '{preset.Name}'?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _store.Delete(preset.Id);
    }

    [RelayCommand]
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Presets JSON (*.json)|*.json", Title = "Importar presets" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var added = _store.ImportJson(File.ReadAllText(dialog.FileName));
            StatusMessage = $"Importados {added.Count} preset(s).";
        }
        catch (Exception ex) { StatusMessage = $"Error al importar: {ex.Message}"; }
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Presets JSON (*.json)|*.json", Title = "Exportar presets", FileName = "baioss-presets.json"
        };
        if (dialog.ShowDialog() != true) return;
        // Exporta el seleccionado si lo hay; si no, todos.
        var ids = SelectedPreset is null ? Array.Empty<Guid>() : new[] { SelectedPreset.Id };
        File.WriteAllText(dialog.FileName, _store.ExportJson(ids));
        StatusMessage = $"Exportado a {Path.GetFileName(dialog.FileName)}.";
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        TargetChannel!.ApplyPreset(SelectedPreset!);
        StatusMessage = $"Preset '{SelectedPreset!.Name}' aplicado al Canal {TargetChannel!.Key}.";
    }

    private void SelectAfterRefresh(Guid id)
    {
        Refresh();
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == id) ?? SelectedPreset;
    }

    private static bool ShowEditor(EncodingPreset preset)
    {
        var window = new PresetEditorWindow { DataContext = new PresetEditorViewModel(preset), Owner = System.Windows.Application.Current?.MainWindow };
        return window.ShowDialog() == true;
    }
}
