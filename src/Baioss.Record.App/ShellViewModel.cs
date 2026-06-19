using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Presets;
using Baioss.Record.App.Preview;
using Baioss.Record.App.Presets;
using Baioss.Record.App.Recording;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel raíz del shell. Expone un <see cref="ChannelViewModel"/> por canal registrado,
/// ordenados por su clave (A, B, …), y abre el gestor de presets de grabación/encoding.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IPresetStore _presetStore;

    public ObservableCollection<ChannelViewModel> Channels { get; }

    public ShellViewModel(IChannelManager channels, PreviewCatalog previews, RecordingCapabilities capabilities, IPresetStore presetStore)
    {
        _presetStore = presetStore;
        Channels = new ObservableCollection<ChannelViewModel>(
            channels.Channels
                .OrderBy(e => e.Status.Key, StringComparer.Ordinal)
                .Select(e => new ChannelViewModel(e, previews.For(e.ChannelId), capabilities.GpuEncoders)));
    }

    [RelayCommand]
    private void OpenPresets()
    {
        var viewModel = new PresetManagerViewModel(_presetStore, Channels.ToList());
        var window = new PresetManagerWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Show();
    }
}
