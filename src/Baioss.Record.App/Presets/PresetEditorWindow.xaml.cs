using System.Windows;
using Baioss.Record.App.Localization;

namespace Baioss.Record.App.Presets;

/// <summary>Diálogo de edición de un preset personalizado. DataContext = PresetEditorViewModel.</summary>
public partial class PresetEditorWindow : SecondaryWindow
{
    public PresetEditorWindow() => InitializeComponent();

    private void Ok(object sender, RoutedEventArgs e)
    {
        // Un preset que no podría grabar no se guarda: se dice por qué y el editor sigue abierto para corregirlo.
        if (DataContext is PresetEditorViewModel vm && vm.Problems() is { Count: > 0 } problems)
        {
            MessageBox.Show(this, Loc.F("PreEd_Invalid_Body", string.Join("\n", problems.Select(p => "• " + p))),
                Loc.T("PreEd_Invalid_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
