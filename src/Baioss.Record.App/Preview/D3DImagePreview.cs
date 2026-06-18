using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DX9 = Vortice.Direct3D9;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App.Preview;

/// <summary>
/// Render del preview por GPU: una textura D3D11 compartida que WPF compone vía
/// <see cref="D3DImage"/> a través del puente D3D9Ex (interop con handle compartido). Los
/// frames BGRA llegan por CPU (una textura staging) y se copian a la textura compartida en
/// VRAM; WPF lee esa textura directamente (sin copia de vuelta a CPU). La ruta de cero copias
/// NVDEC→D3D11 sustituiría solo el origen de la textura, manteniendo este puente.
/// </summary>
internal sealed class D3DImagePreview : IDisposable
{
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();

    private readonly int _width;
    private readonly int _height;
    private readonly Int32Rect _rect;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _shared;   // textura compartida (la lee D3D9/WPF)
    private readonly ID3D11Texture2D _staging;   // CPU-write para subir cada frame
    private readonly DX9.IDirect3D9Ex _d3d9;
    private readonly DX9.IDirect3DDevice9Ex _device9;
    private readonly DX9.IDirect3DTexture9 _tex9;
    private readonly DX9.IDirect3DSurface9 _surface9;

    public D3DImage Image { get; }

    private D3DImagePreview(int width, int height,
        ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D shared, ID3D11Texture2D staging,
        DX9.IDirect3D9Ex d3d9, DX9.IDirect3DDevice9Ex device9, DX9.IDirect3DTexture9 tex9, DX9.IDirect3DSurface9 surface9)
    {
        _width = width; _height = height;
        _rect = new Int32Rect(0, 0, width, height);
        _device = device; _context = context; _shared = shared; _staging = staging;
        _d3d9 = d3d9; _device9 = device9; _tex9 = tex9; _surface9 = surface9;

        Image = new D3DImage();
        Image.Lock();
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface9.NativePointer);
        Image.Unlock();
    }

    /// <summary>Intenta construir la ruta GPU; devuelve null ante cualquier fallo de interop.</summary>
    public static D3DImagePreview? TryCreate(int width, int height)
    {
        ID3D11Device? device = null; ID3D11DeviceContext? context = null;
        ID3D11Texture2D? shared = null; ID3D11Texture2D? staging = null;
        DX9.IDirect3D9Ex? d3d9 = null; DX9.IDirect3DDevice9Ex? device9 = null;
        DX9.IDirect3DTexture9? tex9 = null; DX9.IDirect3DSurface9? surface9 = null;
        try
        {
            var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            D3D11.D3D11CreateDevice(null!, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                featureLevels, out device, out context).CheckError();

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared,
            };
            shared = device.CreateTexture2D(desc);

            var stagingDesc = desc;
            stagingDesc.Usage = ResourceUsage.Staging;
            stagingDesc.BindFlags = BindFlags.None;
            stagingDesc.CPUAccessFlags = CpuAccessFlags.Write;
            stagingDesc.MiscFlags = ResourceOptionFlags.None;
            staging = device.CreateTexture2D(stagingDesc);

            using var dxgiResource = shared.QueryInterface<IDXGIResource>();
            nint sharedHandle = dxgiResource.SharedHandle;

            nint hwnd = GetDesktopWindow();
            d3d9 = DX9.D3D9.Direct3DCreate9Ex();
            var pp = new DX9.PresentParameters
            {
                Windowed = true,
                SwapEffect = DX9.SwapEffect.Discard,
                DeviceWindowHandle = hwnd,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = DX9.Format.X8R8G8B8,
                PresentationInterval = DX9.PresentInterval.Immediate,
            };
            device9 = d3d9.CreateDeviceEx(0, DX9.DeviceType.Hardware, hwnd,
                DX9.CreateFlags.HardwareVertexProcessing | DX9.CreateFlags.Multithreaded | DX9.CreateFlags.FpuPreserve,
                pp);

            // Abre la MISMA superficie en D3D9 a partir del handle compartido de D3D11.
            tex9 = device9.CreateTexture((uint)width, (uint)height, 1, DX9.Usage.RenderTarget,
                DX9.Format.A8R8G8B8, DX9.Pool.Default, ref sharedHandle);
            surface9 = tex9.GetSurfaceLevel(0);

            return new D3DImagePreview(width, height, device, context, shared, staging, d3d9, device9, tex9, surface9);
        }
        catch
        {
            surface9?.Dispose(); tex9?.Dispose(); device9?.Dispose(); d3d9?.Dispose();
            staging?.Dispose(); shared?.Dispose(); context?.Dispose(); device?.Dispose();
            return null; // → el llamador usa el fallback WriteableBitmap
        }
    }

    /// <summary>Sube un frame BGRA a la textura compartida y marca la región sucia. Hilo de UI.</summary>
    public void Update(PreviewFrame frame)
    {
        var map = _context.Map(_staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (int y = 0; y < _height; y++)
                Marshal.Copy(frame.Bgra, y * frame.Stride, IntPtr.Add(map.DataPointer, y * (int)map.RowPitch), frame.Stride);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
        _context.CopyResource(_shared, _staging);
        _context.Flush();

        if (!Image.IsFrontBufferAvailable) return;
        Image.Lock();
        try { Image.AddDirtyRect(_rect); }
        finally { Image.Unlock(); }
    }

    public void Dispose()
    {
        _surface9?.Dispose();
        _tex9?.Dispose();
        _device9?.Dispose();
        _d3d9?.Dispose();
        _staging?.Dispose();
        _shared?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
