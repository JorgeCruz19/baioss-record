using System.Windows;

namespace Baioss.Record.App.Audit;

/// <summary>Ventana del registro de actividad (auditoría). El <see cref="AuditViewModel"/> se inyecta como
/// DataContext desde el shell al abrirla.</summary>
public partial class AuditWindow : SecondaryWindow
{
    public AuditWindow()
    {
        InitializeComponent();
        // El ViewModel se suscribe al cambio de idioma (estático): si no se suelta al cerrar, cada apertura
        // dejaría uno retenido y todos recargarían a la vez al cambiar de idioma.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    // La ventana se abre con Show() (no modal): IsCancel no la cierra (intentaría fijar DialogResult y falla).
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
