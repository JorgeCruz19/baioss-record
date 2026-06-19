using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Presets;
using Baioss.Record.App.Inputs;
using Baioss.Record.App.Preview;
using Baioss.Record.App.Presets;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel raíz del shell. Expone un <see cref="ChannelViewModel"/> por canal, abre el gestor de
/// presets de encoding y el de entradas (asignar tarjeta/cámara a un canal). Cuando el
/// <see cref="ChannelHost"/> reconstruye un canal en caliente, reemplaza su ViewModel para que la
/// vista re-enlace el preview de la nueva entrada.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ChannelHost _host;
    private readonly PreviewCatalog _previews;
    private readonly IPresetStore _presetStore;
    private readonly IDeviceEnumerator _devices;

    public ObservableCollection<ChannelViewModel> Channels { get; }

    public ShellViewModel(ChannelHost host, PreviewCatalog previews, IPresetStore presetStore, IDeviceEnumerator devices)
    {
        _host = host;
        _previews = previews;
        _presetStore = presetStore;
        _devices = devices;

        Channels = new ObservableCollection<ChannelViewModel>(
            host.Channels
                .OrderBy(e => e.Status.Key, StringComparer.Ordinal)
                .Select(e => new ChannelViewModel(e, previews.For(e.ChannelId))));

        _host.ChannelRebound += OnChannelRebound;
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

    [RelayCommand]
    private void OpenInputs()
    {
        var viewModel = new InputsManagerViewModel(_devices, Channels.ToList(), _host.CanRebind, _host.DemoClipPath, RebindAsync);
        var window = new InputsManagerWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Show();
    }

    private Task RebindAsync(Guid channelId, InputSource def) => _host.RebindAsync(channelId, def);

    /// <summary>Tras reconstruir un canal, reemplaza su ViewModel (misma posición) para re-enlazar el preview.</summary>
    private void OnChannelRebound(Guid channelId)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < Channels.Count; i++)
            {
                if (Channels[i].ChannelId != channelId) continue;
                Channels[i].Dispose();
                Channels[i] = new ChannelViewModel(_host.Get(channelId), _previews.For(channelId));
                break;
            }
        });
    }
}
