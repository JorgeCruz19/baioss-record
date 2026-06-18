using System.Windows;

namespace Baioss.Record.App;

/// <summary>Ventana principal (shell) de dos canales. Recibe el ShellViewModel por DI.</summary>
public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
