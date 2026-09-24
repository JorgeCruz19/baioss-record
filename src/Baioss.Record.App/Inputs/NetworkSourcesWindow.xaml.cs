using System.Windows;

namespace Baioss.Record.App.Inputs;

/// <summary>Diálogo de alta/edición/baja de fuentes de red (SRT/RTMP). DataContext = <see cref="NetworkSourcesViewModel"/>.</summary>
public partial class NetworkSourcesWindow : Window
{
    public NetworkSourcesWindow() => InitializeComponent();

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
