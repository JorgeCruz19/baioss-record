using System.Windows;

namespace Baioss.Record.App.Recordings;

/// <summary>Ventana del historial de grabaciones (lista + marcado de protección). El <see cref="RecordingsViewModel"/>
/// se inyecta como DataContext desde el shell al abrirla.</summary>
public partial class RecordingsWindow : Window
{
    public RecordingsWindow() => InitializeComponent();

    // La ventana se abre con Show() (no modal): IsCancel no la cierra (intentaría fijar DialogResult y falla).
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
