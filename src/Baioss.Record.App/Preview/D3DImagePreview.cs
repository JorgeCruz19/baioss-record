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
/// VRAM; WPF lee esa textura directamente (sin copia de vuelta a CPU).
///
/// <para>Resistente a la PÉRDIDA DEL FRONT BUFFER y del DISPOSITIVO. El compositor de WPF rehace su dispositivo al
/// arrancar (en este equipo, siempre: el front buffer de todas las <see cref="D3DImage"/> cae y vuelve unos 30 ms
/// después), al bloquear la sesión y en un reset del GPU (energía/MUX del portátil, TDR). Mientras no hay front buffer
/// no se presenta nada, pero se CONSERVA el back buffer: soltándolo, la <see cref="D3DImage"/> no se enteraba de que el
/// front buffer volvía y el preview se quedaba en negro para siempre mientras seguía recibiendo cuadros (al arrancar
/// con dos canales, casi siempre el primero, hasta reasignar su entrada; medido el 2026-10-19). Cuando vuelve (evento
/// <see cref="D3DImage.IsFrontBufferAvailableChanged"/>) se le vuelve a dar el back buffer, como pide WPF; solo si NUESTROS
/// dispositivos también se perdieron (o cuando una subida de frame falla) se RECONSTRUYEN los recursos nativos,
/// conservando el mismo <see cref="D3DImage"/> (sin tocar el binding). Si aun así no se recupera, o si el front buffer
/// no vuelve, el llamador (<c>PreviewSurface</c>) cae a CPU.</para>
/// </summary>
internal sealed class D3DImagePreview : IDisposable
{
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();

    private readonly int _width;
    private readonly int _height;
    private readonly Int32Rect _rect;

    private Gpu? _gpu;                 // recursos nativos; null mientras el dispositivo está perdido
    private long _nextRebuildTick;     // anti-rebote de reconstrucción (Environment.TickCount64)
    private bool _disposed;

    public D3DImage Image { get; }

    private D3DImagePreview(int width, int height, Gpu gpu)
    {
        _width = width; _height = height;
        _rect = new Int32Rect(0, 0, width, height);
        _gpu = gpu;

        Image = new D3DImage();
        // Cuando el front buffer del compositor de WPF vuelve, se le vuelve a dar el back buffer (y, si NUESTROS
        // dispositivos también se perdieron en un reset del GPU, se reconstruyen).
        Image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        SetBackBuffer(gpu);
    }

    /// <summary>Intenta construir la ruta GPU; devuelve null ante cualquier fallo de interop.</summary>
    public static D3DImagePreview? TryCreate(int width, int height)
    {
        var gpu = Gpu.TryBuild(width, height);
        return gpu is null ? null : new D3DImagePreview(width, height, gpu);
    }

    /// <summary>
    /// Sube un frame BGRA a la textura compartida y lo presenta. Devuelve <c>false</c> si AHORA no se puede presentar
    /// —sin front buffer, o con el dispositivo GPU perdido— para que el llamador caiga a CPU si no se recupera. Hilo de UI.
    /// </summary>
    public bool Update(PreviewFrame frame)
    {
        if (_disposed) return true;
        // Sin front buffer no se sube ni se reconstruye nada: lo re-arma OnFrontBufferAvailableChanged cuando vuelve.
        if (!Image.IsFrontBufferAvailable) return false;
        if (_gpu is null) { TryRebuild(); if (_gpu is null) return false; }

        var gpu = _gpu;
        try
        {
            var map = gpu.Context.Map(gpu.Staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (int y = 0; y < _height; y++)
                    Marshal.Copy(frame.Bgra, y * frame.Stride, IntPtr.Add(map.DataPointer, y * (int)map.RowPitch), frame.Stride);
            }
            finally { gpu.Context.Unmap(gpu.Staging, 0); }
            gpu.Context.CopyResource(gpu.Shared, gpu.Staging);
            gpu.Context.Flush();
        }
        catch
        {
            // Dispositivo perdido/removido (reset del GPU al conmutar energía/MUX, TDR…): suelta y reintenta.
            LoseGpu("excepción al subir el frame");
            return false;
        }

        // Detección AUTORITATIVA: en híbridos el puente puede quedar inválido sin excepción ni bajar el
        // front buffer → consultamos el estado del dispositivo y, si se perdió, reconstruimos.
        if (gpu.IsLost()) { LoseGpu("estado de dispositivo perdido"); return false; }

        Image.Lock();
        try { Image.AddDirtyRect(_rect); }
        finally { Image.Unlock(); }
        return true;
    }

    private void OnFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        // Fuera de servicio: no se presenta (ver Update), pero el back buffer y los recursos se CONSERVAN. Soltar el back
        // buffer aquí dejaba a la D3DImage sin enterarse de que el front buffer volvía: negro para siempre.
        if (!Image.IsFrontBufferAvailable) return;
        // Volvió. Lo normal (al arrancar, al bloquear la sesión) es que NUESTROS dispositivos sigan sanos: basta con volver a
        // darle el back buffer, como pide WPF. Si también se perdieron en un reset del GPU, se reconstruyen y re-arman.
        if (_gpu is { } gpu && !gpu.IsLost()) { SetBackBuffer(gpu); return; }
        DropGpu();
        _nextRebuildTick = 0;     // permite un intento inmediato
        TryRebuild();
    }

    private void TryRebuild()
    {
        long now = Environment.TickCount64;
        if (now < _nextRebuildTick) return;   // como máximo un intento cada 500 ms
        _nextRebuildTick = now + 500;

        var gpu = Gpu.TryBuild(_width, _height);
        if (gpu is null) return;
        _gpu = gpu;
        SetBackBuffer(gpu);
        Serilog.Log.Information("Preview: dispositivo GPU reconstruido tras el reset.");
    }

    private void LoseGpu(string why)
    {
        Serilog.Log.Warning("Preview: dispositivo GPU perdido ({Why}); reconstruyendo…", why);
        DropGpu();
    }

    private void SetBackBuffer(Gpu gpu)
    {
        Image.Lock();
        try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, gpu.Surface9.NativePointer); }
        finally { Image.Unlock(); }
    }

    private void DropGpu()
    {
        try { _gpu?.Dispose(); } catch { /* el dispositivo ya pudo quedar inválido */ }
        _gpu = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Image.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
        DropGpu();
    }

    /// <summary>Recursos nativos D3D11 + puente D3D9Ex. Se construyen/destruyen como un todo (rebuild atómico).</summary>
    private sealed class Gpu : IDisposable
    {
        public required ID3D11Device Device { get; init; }
        public required ID3D11DeviceContext Context { get; init; }
        public required ID3D11Texture2D Shared { get; init; }   // textura compartida (la lee D3D9/WPF)
        public required ID3D11Texture2D Staging { get; init; }  // CPU-write para subir cada frame
        public required DX9.IDirect3D9Ex D3d9 { get; init; }
        public required DX9.IDirect3DDevice9Ex Device9 { get; init; }
        public required DX9.IDirect3DTexture9 Tex9 { get; init; }
        public required DX9.IDirect3DSurface9 Surface9 { get; init; }
        public required nint Hwnd { get; init; }

        /// <summary>
        /// ¿El dispositivo se perdió/reseteó? En portátiles híbridos la conmutación de MUX/energía puede
        /// dejar el puente inválido SIN lanzar excepción ni bajar <c>IsFrontBufferAvailable</c>; la consulta
        /// autoritativa es el estado del propio dispositivo (D3D11 removido y/o D3D9Ex perdido).
        /// </summary>
        public bool IsLost()
        {
            try
            {
                if (Device.DeviceRemovedReason.Failure) return true; // D3D11: adaptador removido/reseteado
                Device9.CheckDeviceState(Hwnd);                       // D3D9Ex: lanza (SharpGenException) si se perdió
                return false;
            }
            catch { return true; } // la consulta lanzó (dispositivo perdido/colgado) → reconstruir
        }

        public static Gpu? TryBuild(int width, int height)
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

                return new Gpu
                {
                    Device = device, Context = context, Shared = shared, Staging = staging,
                    D3d9 = d3d9, Device9 = device9, Tex9 = tex9, Surface9 = surface9, Hwnd = hwnd,
                };
            }
            catch
            {
                surface9?.Dispose(); tex9?.Dispose(); device9?.Dispose(); d3d9?.Dispose();
                staging?.Dispose(); shared?.Dispose(); context?.Dispose(); device?.Dispose();
                return null; // → el llamador usa el fallback WriteableBitmap
            }
        }

        public void Dispose()
        {
            Surface9?.Dispose();
            Tex9?.Dispose();
            Device9?.Dispose();
            D3d9?.Dispose();
            Staging?.Dispose();
            Shared?.Dispose();
            Context?.Dispose();
            Device?.Dispose();
        }
    }
}
