using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Baioss.Record.App.Preview;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App;

/// <summary>Vista de un canal. Su DataContext es un <see cref="ChannelViewModel"/>.</summary>
public partial class ChannelView : UserControl
{
    private IChannelPreviewSource? _preview;
    private PreviewSurface? _surface;

    // PRESENTACIÓN. Los frames llegan en un hilo de fondo y se copian a búferes propios (el del motor se reutiliza en
    // seguida); el planificador les da turno a la cadencia de la fuente y en cada refresco de pantalla
    // (CompositionTarget.Rendering) se sube a la superficie el que toca. Pintar cada frame al llegar repartía el jitter de
    // la llegada entre los refrescos: en un monitor de 60 Hz, uno de cada cinco cuadros se veía uno o tres refrescos en vez
    // de dos (ver PreviewPresentationScheduler). Todo lo compartido entre los dos hilos va bajo _sync.
    private readonly object _sync = new();
    private readonly PreviewPresentationScheduler<byte[]> _scheduler = new();
    private readonly Stack<byte[]> _free = new();
    private readonly List<byte[]> _recycled = new();
    private int _width, _height, _frameBytes;
    private bool _rendering;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

    // Diagnóstico (registro): el primero a los 30 s de enlazar, luego cada PreviewPresentationSettings.DiagnosticsIntervalSeconds.
    private readonly PreviewPresentationStats _stats = new();
    private double _nextReport;

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

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // La superficie cambió su ImageSource (cayó a CPU porque la GPU no se recuperó): re-apunta el control.
    private void OnSurfaceImageSourceChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _surface) && _surface is not null)
            PreviewImage.Source = _surface.ImageSource;
    }

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
        if (_surface is not null) _surface.ImageSourceChanged -= OnSurfaceImageSourceChanged;
        StopRendering();
        _surface?.Dispose();
        _surface = null;
        lock (_sync)
        {
            // Los frames en cola son de la fuente anterior (quizá de otro tamaño): fuera, con sus búferes.
            _scheduler.Clear(_recycled);
            _recycled.Clear();
            _free.Clear();
            _preview = preview;
            _width = preview?.FrameWidth ?? 0;
            _height = preview?.FrameHeight ?? 0;
            _frameBytes = _width * 4 * _height;
            _stats.Close(Now(), _scheduler.LatePushes, _scheduler.Skipped);
        }

        if (preview is null) { PreviewImage.Source = null; return; }

        var surface = new PreviewSurface(preview.FrameWidth, preview.FrameHeight);
        _surface = surface;
        PreviewImage.Source = surface.ImageSource;
        surface.ImageSourceChanged += OnSurfaceImageSourceChanged; // p. ej. si la GPU no vuelve y cae a CPU
        preview.FrameReady += OnFrameReady;
        _nextReport = Now() + 30;
        StartRendering();
        Serilog.Log.Information("Preview canal {Key}: render {Mode}.",
            (DataContext as ChannelViewModel)?.Key, surface.IsGpu ? "D3D11/D3DImage (GPU)" : "WriteableBitmap (CPU)");
    }

    private void StartRendering()
    {
        if (_rendering) return;
        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
    }

    // CompositionTarget.Rendering es un evento ESTÁTICO: sin desuscribirse, la vista no se liberaría nunca.
    private void StopRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    // Llega en un hilo de fondo, a la cadencia de la fuente (con su jitter): se copia el frame y se le da turno.
    private void OnFrameReady(object? sender, PreviewFrame frame)
    {
        double now = Now(); // la llegada: antes de copiar
        byte[]? buffer;
        int bytes;
        lock (_sync)
        {
            // Un FrameReady YA en curso (invocación capturada antes del `-=` de BindPreview) puede llegar tras un rebind:
            // se descarta si NO viene de la fuente vigente (quizá de otra resolución). (N25.)
            if (!ReferenceEquals(sender, _preview) || frame.Width != _width || frame.Height != _height) return;
            bytes = _frameBytes;
            buffer = _free.Count > 0 ? _free.Pop() : null;
        }
        if (frame.Bgra.Length < bytes) return;
        buffer ??= new byte[bytes];
        Buffer.BlockCopy(frame.Bgra, 0, buffer, 0, bytes);
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _preview) || buffer.Length != _frameBytes) return; // rebind durante la copia
            // Una fuente que solo trae sus cuadros reales (NDI a saltos) se enseña al llegar: su llegada es su ritmo.
            _scheduler.Enqueue(buffer, now, _recycled, frame.AtSourcePace);
            _stats.OnArrival(now, _scheduler.LastLead);
            Recycle();
        }
    }

    // Un refresco de pantalla (hilo de UI): se sube el frame cuyo turno llegó, si lo hay.
    private void OnRendering(object? sender, EventArgs e)
    {
        var surface = _surface;
        if (surface is null) return;
        // WPF puede elevar Rendering más de una vez para el mismo fotograma (mismo RenderingTime): subir dos frames en un
        // mismo refresco dejaría uno sin verse.
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = r.RenderingTime;
        }
        double now = Now();
        byte[]? show;
        lock (_sync)
        {
            show = _scheduler.Take(now, _recycled);
            _stats.OnTick(now, show is not null);
            Recycle();
        }
        if (show is not null)
        {
            // La superficie COPIA el frame (WritePixels / textura de subida): después el búfer se puede reutilizar.
            try { surface.Push(new PreviewFrame(show, _width, _height, _width * 4)); }
            finally { lock (_sync) { if (show.Length == _frameBytes) _free.Push(show); } }
        }
        if (now >= _nextReport) ReportPresentation(now, surface.IsGpu);
    }

    private void Recycle()
    {
        foreach (var b in _recycled)
            if (b.Length == _frameBytes) _free.Push(b);
        _recycled.Clear();
    }

    private void ReportPresentation(double now, bool gpu)
    {
        int interval = PreviewPresentationSettings.DiagnosticsIntervalSeconds;
        _nextReport = interval > 0 ? now + interval : double.MaxValue;
        PreviewPresentationReport? r;
        lock (_sync) r = _stats.Close(now, _scheduler.LatePushes, _scheduler.Skipped);
        if (interval <= 0 || r is not { } x) return;
        Serilog.Log.Information(string.Create(CultureInfo.InvariantCulture,
            $"Preview canal {(DataContext as ChannelViewModel)?.Key}: fuente {x.SourceFps:0.00} fps, pantalla {x.DisplayHz:0.0} Hz, {x.IrregularPercent:0.0} % de cuadros con duración irregular, retardo {x.MedianLeadMs:0} ms (máx {x.MaxLeadMs:0}), {x.Late} tarde, {x.Skipped} sin enseñar, render {(gpu ? "GPU" : "CPU")}."));
    }
}
