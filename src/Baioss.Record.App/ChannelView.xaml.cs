using System.Windows;
using System.Windows.Controls;
using Baioss.Record.App.Preview;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App;

/// <summary>Vista de un canal. Su DataContext es un <see cref="ChannelViewModel"/>.</summary>
public partial class ChannelView : UserControl
{
    private IChannelPreviewSource? _preview;
    private PreviewSurface? _surface;
    private PreviewFrame? _pending;
    private bool _renderQueued;

    public ChannelView()
    {
        InitializeComponent();
        // El preview se (re)enlaza ante CUALQUIERA de estos eventos, de forma idempotente:
        //  - Loaded: primera materialización de la vista.
        //  - DataContextChanged: el canal se reconstruyó en caliente (RebindAsync cambió la entrada)
        //    y el shell sustituyó el ViewModel; aquí hay que apuntar a la NUEVA fuente de preview.
        //    Sin esto, el ItemsControl puede reutilizar el contenedor y la vista quedaría enganchada a
        //    la fuente vieja (ya dispuesta) → el preview se "congela" en el último frame previo.
        Loaded += (_, _) => BindPreview(CurrentPreview);
        DataContextChanged += (_, e) => BindPreview((e.NewValue as ChannelViewModel)?.Preview);
        Unloaded += (_, _) => BindPreview(null);
    }

    private IChannelPreviewSource? CurrentPreview => (DataContext as ChannelViewModel)?.Preview;

    /// <summary>
    /// Engancha la vista a una fuente de preview (o la desengancha con <c>null</c>). Idempotente: si ya
    /// está enlazada a la misma fuente no hace nada. Al cambiar de fuente, desmonta la superficie y la
    /// suscripción anteriores antes de montar las nuevas.
    /// </summary>
    private void BindPreview(IChannelPreviewSource? preview)
    {
        if (ReferenceEquals(preview, _preview)) return;

        // Desengancha la fuente anterior y libera su superficie.
        if (_preview is not null) _preview.FrameReady -= OnFrameReady;
        _surface?.Dispose();
        _surface = null;
        _pending = null;           // descarta cualquier frame en cola de la fuente anterior
        _preview = preview;

        if (preview is null) { PreviewImage.Source = null; return; }

        var surface = new PreviewSurface(preview.FrameWidth, preview.FrameHeight);
        _surface = surface;
        PreviewImage.Source = surface.ImageSource;
        preview.FrameReady += OnFrameReady;
        Serilog.Log.Information("Preview canal {Key}: render {Mode}.",
            (DataContext as ChannelViewModel)?.Key, surface.IsGpu ? "D3D11/D3DImage (GPU)" : "WriteableBitmap (CPU)");
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
