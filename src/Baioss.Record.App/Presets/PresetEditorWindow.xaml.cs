using System.Windows;

namespace Baioss.Record.App.Presets;

/// <summary>Diálogo de edición de un preset personalizado. DataContext = PresetEditorViewModel.</summary>
public partial class PresetEditorWindow : Window
{
    public PresetEditorWindow() => InitializeComponent();

    private void Ok(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
