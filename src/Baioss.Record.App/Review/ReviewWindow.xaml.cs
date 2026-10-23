using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Baioss.Record.App.Localization;

namespace Baioss.Record.App.Review;

/// <summary>
/// Ventana «Revisar» (se abre desde «Grabaciones» y desde «Borrar grabaciones»). Atajos como los de un editor: Espacio
/// reproduce/para, ← → un cuadro, Mayús+← → un segundo, Inicio/Fin, I y O marcan entrada y salida, Mayús+I / Mayús+O van a
/// ellas. Al cerrarse para el reproductor (sus FFmpeg) y, si hay un clip exportándose, pregunta antes de cancelarlo.
/// </summary>
public partial class ReviewWindow : SecondaryWindow
{
    private bool _closingConfirmed;

    public ReviewWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += async (_, _) => { if (DataContext is ReviewViewModel vm) await vm.DisposeAsync(); };
    }

    private ReviewViewModel? Vm => DataContext as ReviewViewModel;

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Escribiendo el nombre del clip, las teclas son del texto.
        if (e.OriginalSource is TextBox || Vm is not { IsLoaded: true } vm) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Space: vm.PlayPauseCommand.Execute(null); break;
            case Key.Left: (shift ? vm.SecondBackCommand : vm.StepBackCommand).Execute(null); break;
            case Key.Right: (shift ? vm.SecondForwardCommand : vm.StepForwardCommand).Execute(null); break;
            case Key.Home: vm.GoToStartCommand.Execute(null); break;
            case Key.End: vm.GoToEndCommand.Execute(null); break;
            case Key.I: (shift ? (ICommand)vm.GoToInCommand : vm.MarkInCommand).Execute(null); break;
            case Key.O: (shift ? (ICommand)vm.GoToOutCommand : vm.MarkOutCommand).Execute(null); break;
            default: return;
        }
        e.Handled = true; // que Espacio no «pulse» además el botón con el foco
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed || Vm is not { Exporting: true } vm) return;
        var answer = MessageBox.Show(this, Loc.T("Rev_Close_Exporting"), Loc.T("Rev_Header"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        _closingConfirmed = true;
        vm.CancelExportCommand.Execute(null);
    }

    // La ventana se abre con Show() (no modal): IsCancel no la cierra (intentaría fijar DialogResult y falla).
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
