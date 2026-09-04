using Baioss.Record.App.Localization;
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
    // Resuelve el ViewModel VIGENTE de un canal por su Id (contra la colección viva del shell): si hubo un rebind
    // con esta ventana abierta, TargetChannel apunta a un VM ya DISPUESTO. (Auditoría N10.)
    private readonly Func<Guid, ChannelViewModel?>? _resolveChannel;

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

    public PresetManagerViewModel(IPresetStore store, IReadOnlyList<ChannelViewModel> channels,
        Func<Guid, ChannelViewModel?>? resolveChannel = null)
    {
        _store = store;
        Channels = channels;
        _resolveChannel = resolveChannel;
        TargetChannel = channels.FirstOrDefault();

        Categories = new ObservableCollection<CategoryFilter>(new[]
        {
            new CategoryFilter(Loc.T("Pre_Cat_All"), null),
            new CategoryFilter(Loc.T("Pre_Cat_Favorites"), null, true),
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
        string res = p is { Width: { } w, Height: { } h } ? $"{w}×{h}" : Loc.T("Pre_Native");
        string fps = p.FrameRateNum > 0 ? (p.FrameRateNum / (double)p.FrameRateDen).ToString("0.###") : Loc.T("Pre_SourceFps");
        string max = p.MaxBitrateMbps > 0 ? Loc.F("Pre_Max", p.MaxBitrateMbps.ToString("0.#")) : "";
        string vbr = p.AudioOnly ? "—" : $"{p.VideoBitrateMbps:0.#} Mbps{max}";

        // Las etiquetas se RELLENAN a un ancho fijo en vez de escribir los espacios a mano: traducidas cambian
        // de longitud y, con espacios fijos, la columna de valores quedaba desalineada en inglés.
        static string Row(string labelKey, string value) => Loc.T(labelKey).PadRight(15) + value;

        return string.Join('\n', new[]
        {
            Row("Pre_Lbl_Container", p.Container.ToString()),
            Row("Pre_Lbl_AudioOnly", p.AudioOnly ? Loc.T("Pre_Yes") : Loc.T("Pre_No")),
            Row("Pre_Lbl_VideoCodec", p.AudioOnly ? "—" : p.VideoCodec.ToString()),
            Row("Pre_Lbl_Resolution", p.AudioOnly ? "—" : res),
            Row("Pre_Lbl_Fps", p.AudioOnly ? "—" : fps),
            Row("Pre_Lbl_Bitrate", vbr),
            Row("Pre_Lbl_Gop", p.AudioOnly ? "—" : p.GopSize.ToString()),
            Row("Pre_Lbl_PixelFormat", p.AudioOnly ? "—" : p.PixelFormat.ToString()),
            Row("Pre_Lbl_Scan", p.AudioOnly ? "—" : p.ScanType.ToString()),
            Row("Pre_Lbl_RateControl", p.AudioOnly ? "—" : p.RateControl.ToString()),
            Loc.T("Pre_Lbl_AudioSection"),
            Row("Pre_Lbl_AudioCodec", p.AudioCodec.ToString()),
            Row("Pre_Lbl_AudioChannels", $"{p.AudioLayout} ({p.AudioChannels} ch)"),
            Row("Pre_Lbl_SampleRate", $"{p.AudioSampleRate} Hz"),
            Row("Pre_Lbl_AudioBitrate", $"{p.AudioBitrateKbps} kbps"),
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
        if (MessageBox.Show(Loc.F("Pre_Confirm_Delete", preset.Name), Loc.T("Pre_Confirm_Title"),
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
        // Resuelve el VM VIGENTE del canal: si hubo un rebind con la ventana abierta, TargetChannel es un VM
        // dispuesto (motor muerto) y aplicar no llegaría al motor vivo. Re-valida el estado del canal real. (N10.)
        var target = _resolveChannel?.Invoke(TargetChannel!.ChannelId) ?? TargetChannel!;
        if (target is not { IsConfigurable: true, IsRecording: false })
        {
            StatusMessage = Loc.F("Pre_Msg_CannotApply", target.Key);
            return;
        }
        target.ApplyPreset(SelectedPreset!);
        StatusMessage = $"Preset '{SelectedPreset!.Name}' aplicado al Canal {target.Key}.";
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
