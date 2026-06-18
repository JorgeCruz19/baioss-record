using System.Collections.ObjectModel;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel raíz del shell. Expone un <see cref="ChannelViewModel"/> por canal
/// registrado, ordenados por su clave (A, B, …) para el layout de columnas.
/// </summary>
public sealed class ShellViewModel
{
    public ObservableCollection<ChannelViewModel> Channels { get; }

    public ShellViewModel(IChannelManager channels)
    {
        Channels = new ObservableCollection<ChannelViewModel>(
            channels.Channels
                .OrderBy(e => e.Status.Key, StringComparer.Ordinal)
                .Select(e => new ChannelViewModel(e)));
    }
}
