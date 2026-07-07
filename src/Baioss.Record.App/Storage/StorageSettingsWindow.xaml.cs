using System.Windows;

namespace Baioss.Record.App.Storage;

/// <summary>Ventana de ajustes de almacenamiento (retención + alertas + emergencia). El
/// <see cref="StorageSettingsViewModel"/> se inyecta como DataContext desde el shell.</summary>
public partial class StorageSettingsWindow : Window
{
    public StorageSettingsWindow() => InitializeComponent();

    // La ventana se abre con Show() (no modal): IsCancel no la cerraría (fijar DialogResult falla). Cerramos a mano.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
