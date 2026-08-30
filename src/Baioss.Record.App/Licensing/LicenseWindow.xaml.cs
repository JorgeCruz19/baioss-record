using System.Windows;

namespace Baioss.Record.App.Licensing;

public partial class LicenseWindow : Window
{
    public LicenseWindow() => InitializeComponent();

    // La ventana se abre con Show() (NO modal): con IsCancel="True" el botón intentaría fijar DialogResult y
    // lanzaría —el handler global se traga la excepción y el botón «no hace nada»—. Se cierra explícitamente.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
