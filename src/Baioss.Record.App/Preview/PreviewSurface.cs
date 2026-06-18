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
/// </summary>
public sealed class PreviewSurface : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly Int32Rect _rect;
    private readonly D3DImagePreview? _d3d;       // ruta GPU (null si no se pudo inicializar)
    private readonly WriteableBitmap? _bitmap;    // fallback CPU

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

    /// <summary>Fuente de imagen a enlazar en el control <c>Image</c>.</summary>
    public ImageSource ImageSource { get; }

    /// <summary>True si el preview se está componiendo por GPU (D3D11/D3DImage).</summary>
    public bool IsGpu => _d3d is not null;

    /// <summary>Sube un frame. Debe invocarse en el hilo de UI.</summary>
    public void Push(PreviewFrame frame)
    {
        if (_d3d is not null) _d3d.Update(frame);
        else _bitmap!.WritePixels(_rect, frame.Bgra, frame.Stride, 0);
    }

    public void Dispose() => _d3d?.Dispose();
}
