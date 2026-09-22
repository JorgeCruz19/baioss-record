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

    /// <summary>Sección «Panel web y API» (null si el host no la ofrece: no debería, pero la ventana abre igual).</summary>
    public ApiAccessViewModel? Api { get; }

    public GeneralSettingsViewModel(IReadOnlyList<ChannelViewModel> channels, ApiAccessViewModel? api = null)
    {
        Channels = channels;
        Api = api;
    }
}
