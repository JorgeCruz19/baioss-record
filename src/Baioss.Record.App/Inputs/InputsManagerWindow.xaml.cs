using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Baioss.Record.App.Inputs;

/// <summary>Ventana de asignación de entradas a canales. Su DataContext es un <see cref="InputsManagerViewModel"/>.</summary>
public partial class InputsManagerWindow : Window
{
    public InputsManagerWindow() => InitializeComponent();

    /// <summary>Abre un enlace en el navegador del sistema (la atribución de NDI exige un enlace a ndi.video).
    /// <c>UseShellExecute</c> es imprescindible: sin él, Process.Start no sabe abrir una URL.</summary>
    private void OpenLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.Exception ex) { Serilog.Log.Warning(ex, "No se pudo abrir el enlace {Url}.", e.Uri); }
        e.Handled = true;
    }
}
