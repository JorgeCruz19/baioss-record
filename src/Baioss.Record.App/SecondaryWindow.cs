using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Baioss.Record.App;

/// <summary>
/// Base de TODAS las ventanas que no son la principal (presets, entradas, programación, configuración, diálogos…).
/// Reúne dos reglas que cada ventana olvidaba por separado:
/// <list type="bullet">
///   <item><b>Alto máximo:</b> como mucho el <see cref="MaxHeightRatio"/> del alto útil del monitor (sin la barra de
///   tareas), así nunca se sale de la pantalla en un portátil o con escalado de Windows. Lo que no quepa se desplaza:
///   cada ventana pone su contenido variable dentro de un <c>ScrollViewer</c>.</item>
///   <item><b>Al cerrarse devuelve el foco a su dueño.</b> Medido (2026-09-24): tras abrir y cerrar el editor modal desde
///   «Presets», al cerrar «Presets» Windows activaba OTRA aplicación y la ventana principal quedaba detrás, como si la
///   app se hubiera minimizado. Activando el dueño antes de destruir la ventana, Windows ya no tiene que elegir.</item>
/// </list>
/// </summary>
public class SecondaryWindow : Window
{
    /// <summary>Fracción del alto útil del monitor que puede ocupar una ventana secundaria.</summary>
    public const double MaxHeightRatio = 0.9;

    public SecondaryWindow()
    {
        // Antes de mostrarse (el centrado sobre el dueño ya usa el alto acotado). Referencia: la ventana principal, que
        // es donde se abren todas; al asignarse el dueño se recalcula con su monitor.
        MaxHeight = MaxHeightFor(System.Windows.Application.Current?.MainWindow);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MaxHeight = MaxHeightFor(Owner ?? System.Windows.Application.Current?.MainWindow);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !IsActive) return;
        // Solo si el dueño puede recibir el foco: si está deshabilitado es que hay un diálogo modal en curso, y WPF ya
        // devuelve el foco a quien lo tenía al cerrarse ese diálogo.
        if (Owner is { IsVisible: true } owner && owner.WindowState != WindowState.Minimized && IsEnabledWindow(owner))
            owner.Activate();
    }

    /// <summary>
    /// La ventana de ESTA aplicación que está activa (desde la que el operador lanzó la acción), o la principal: es el
    /// dueño correcto de un diálogo abierto desde un ViewModel, que no conoce su ventana. Con la principal como dueña
    /// de un diálogo lanzado desde otra ventana, la cadena de dueños queda rota (ver el problema del foco arriba).
    /// </summary>
    public static Window? ActiveOwner()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) ?? app.MainWindow;
    }

    /// <summary>Alto máximo (unidades WPF) para una ventana que se abre sobre <paramref name="reference"/>.</summary>
    internal static double MaxHeightFor(Window? reference)
    {
        double workArea = WorkAreaHeight(reference) ?? SystemParameters.WorkArea.Height;
        return Math.Floor(workArea * MaxHeightRatio);
    }

    /// <summary>Alto útil (sin barra de tareas) del monitor que contiene a <paramref name="reference"/>, en unidades
    /// WPF; null si la ventana aún no tiene HWND.</summary>
    private static double? WorkAreaHeight(Window? reference)
    {
        if (reference is null) return null;
        IntPtr hwnd = new WindowInteropHelper(reference).Handle;
        if (hwnd == IntPtr.Zero) return null;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        double scale = VisualTreeHelper.GetDpi(reference).DpiScaleY;
        return (info.Work.Bottom - info.Work.Top) / (scale > 0 ? scale : 1);
    }

    private static bool IsEnabledWindow(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && IsWindowEnabled(hwnd);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);
}
