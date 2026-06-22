using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App.Preview;

/// <summary>
/// Superficie de render del preview de un canal. Sube cada <see cref="PreviewFrame"/> BGRA
/// a una imagen WPF. Intenta primero la ruta GPU (textura D3D11 → <see cref="System.Windows.Interop.D3DImage"/>);
/// si el interop nativo no está disponible, cae a <see cref="WriteableBitmap"/> (CPU) para que el
/// preview funcione siempre. El consumidor llama a <see cref="Push"/> en el hilo de UI.
///
/// <para>La ruta GPU se RECUPERA sola ante un reset del dispositivo (cargador/MUX/TDR). Si, pese a ello,
/// no vuelve dentro de <see cref="FallbackAfterMs"/>, esta superficie cambia a CPU de forma definitiva
/// (que siempre funciona) y avisa con <see cref="ImageSourceChanged"/> para que la vista re-enlace.</para>
/// </summary>
public sealed class PreviewSurface : IDisposable
{
    private const long FallbackAfterMs = 5000; // si la GPU no se recupera en este tiempo, cae a CPU

    private readonly int _width;
    private readonly int _height;
    private readonly Int32Rect _rect;
    private D3DImagePreview? _d3d;       // ruta GPU (null si no se pudo inicializar o tras caer a CPU)
    private WriteableBitmap? _bitmap;    // fallback CPU
    private long _lostSince;             // TickCount64 desde que la GPU se perdió (0 = sana)

    public PreviewSurface(int width, int height)
    {
        _width = width;
        _height = height;
        _rect = new Int32Rect(0, 0, width, height);

        // Intento de ruta GPU; cualquier fallo de interop → fallback CPU.
        _d3d = D3DImagePreview.TryCreate(width, height);
        if (_d3d is null)
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

        ImageSource = (ImageSource?)_d3d?.Image ?? _bitmap!;
    }

    /// <summary>Fuente de imagen a enlazar en el control <c>Image</c>. Puede cambiar si se cae a CPU.</summary>
    public ImageSource ImageSource { get; private set; }

    /// <summary>Se eleva (hilo de UI) cuando <see cref="ImageSource"/> cambia, p. ej. al caer a CPU.</summary>
    public event EventHandler? ImageSourceChanged;

    /// <summary>True si el preview se está componiendo por GPU (D3D11/D3DImage).</summary>
    public bool IsGpu => _d3d is not null;

    /// <summary>Sube un frame. Debe invocarse en el hilo de UI.</summary>
    public void Push(PreviewFrame frame)
    {
        if (_d3d is not null)
        {
            if (_d3d.Update(frame)) { _lostSince = 0; return; }   // GPU sana o ya recuperada

            // GPU perdida AHORA. Si no se recupera dentro de la gracia, cae a CPU (que siempre funciona).
            long now = Environment.TickCount64;
            if (_lostSince == 0) _lostSince = now;
            else if (now - _lostSince >= FallbackAfterMs) FallBackToCpu(frame);
            return;
        }
        _bitmap!.WritePixels(_rect, frame.Bgra, frame.Stride, 0);
    }

    /// <summary>Cambia definitivamente a CPU cuando la GPU no se recuperó tras el reset.</summary>
    private void FallBackToCpu(PreviewFrame frame)
    {
        var bmp = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(_rect, frame.Bgra, frame.Stride, 0);
        _bitmap = bmp;
        _d3d?.Dispose();
        _d3d = null;
        _lostSince = 0;
        ImageSource = bmp;
        Serilog.Log.Warning("Preview: la ruta GPU no se recuperó tras el reset; se cambia a CPU (WriteableBitmap).");
        ImageSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _d3d?.Dispose();
}
