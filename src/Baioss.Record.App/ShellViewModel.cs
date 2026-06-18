using System.Collections.ObjectModel;
using Baioss.Record.Application.Channels;
using Baioss.Record.App.Preview;
using Baioss.Record.App.Recording;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel raíz del shell. Expone un <see cref="ChannelViewModel"/> por canal
/// registrado, ordenados por su clave (A, B, …) para el layout de columnas, enlazando
/// cada uno con su motor de preview y las capacidades de codificación del equipo.
/// </summary>
public sealed class ShellViewModel
{
    public ObservableCollection<ChannelViewModel> Channels { get; }

    public ShellViewModel(IChannelManager channels, PreviewCatalog previews, RecordingCapabilities capabilities)
    {
        Channels = new ObservableCollection<ChannelViewModel>(
            channels.Channels
                .OrderBy(e => e.Status.Key, StringComparer.Ordinal)
                .Select(e => new ChannelViewModel(e, previews.For(e.ChannelId), capabilities.GpuEncoders)));
    }
}
