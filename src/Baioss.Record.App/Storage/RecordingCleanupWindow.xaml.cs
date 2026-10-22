using System.Windows;

namespace Baioss.Record.App.Storage;

/// <summary>Ventana «Borrar grabaciones» (se abre desde «Almacenamiento»). El <see cref="RecordingCleanupViewModel"/> se
/// inyecta como DataContext desde el shell al abrirla.</summary>
public partial class RecordingCleanupWindow : SecondaryWindow
{
    public RecordingCleanupWindow()
    {
        InitializeComponent();
        // El ViewModel se suscribe al cambio de idioma (estático): hay que soltarlo al cerrar.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    // La ventana se abre con Show() (no modal): IsCancel no la cierra (intentaría fijar DialogResult y falla).
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
