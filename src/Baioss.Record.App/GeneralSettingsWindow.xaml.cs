using System.Windows;

namespace Baioss.Record.App;

/// <summary>Ventana de configuración general (carpeta de destino + carta de ajuste por canal).</summary>
public partial class GeneralSettingsWindow : Window
{
    public GeneralSettingsWindow() => InitializeComponent();

    // Abierta con Show() (no modal): IsCancel no la cierra (fijar DialogResult falla). Cerramos a mano.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
