using System.Windows;
using System.Windows.Controls;
using Baioss.Record.App.Preview;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App;

/// <summary>Vista de un canal. Su DataContext es un <see cref="ChannelViewModel"/>.</summary>
public partial class ChannelView : UserControl
{
    private FfmpegPreviewEngine? _preview;
    private PreviewSurface? _surface;
    private PreviewFrame? _pending;
    private bool _renderQueued;

    public ChannelView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_surface is not null) return;                       // ya enlazado
        if (DataContext is not ChannelViewModel { Preview: { } preview }) return;

        _preview = preview;
        _surface = new PreviewSurface(preview.FrameWidth, preview.FrameHeight);
        PreviewImage.Source = _surface.ImageSource;
        _preview.FrameReady += OnFrameReady;
        Serilog.Log.Information("Preview canal {Key}: render {Mode}.",
            (DataContext as ChannelViewModel)?.Key, _surface.IsGpu ? "D3D11/D3DImage (GPU)" : "WriteableBitmap (CPU)");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_preview is not null) _preview.FrameReady -= OnFrameReady;
        _surface?.Dispose();
        _surface = null;
        _preview = null;
    }

    // Llega en un hilo de fondo (~25 fps). Patrón "último frame": si la UI no ha consumido el
    // anterior, lo sustituimos y descartamos el obsoleto, evitando acumular trabajo en el Dispatcher.
    private void OnFrameReady(object? sender, PreviewFrame frame)
    {
        _pending = frame;
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _renderQueued = false;
            var latest = _pending;
            if (latest is not null) _surface?.Push(latest);
        });
    }
}
