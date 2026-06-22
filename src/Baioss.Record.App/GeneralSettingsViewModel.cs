using System.Collections.Generic;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel de la ventana de CONFIGURACIÓN GENERAL: ajustes operativos por canal que antes vivían en el
/// panel del canal — carpeta de destino y carta de ajuste al perder señal. Reutiliza directamente los
/// <see cref="ChannelViewModel"/> (que ya exponen <c>OutputDirectory</c>, <c>BrowseOutputCommand</c>,
/// <c>SlateOnSignalLoss</c> y <c>CanConfigure</c>).
/// </summary>
public sealed class GeneralSettingsViewModel
{
    public IReadOnlyList<ChannelViewModel> Channels { get; }

    public GeneralSettingsViewModel(IReadOnlyList<ChannelViewModel> channels) => Channels = channels;
}
