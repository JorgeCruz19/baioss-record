using NewTek;
using NewTek.NDI;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Acceso SEGURO al runtime NDI (SDK NewTek vía el paquete NDILibDotNetCoreBase). El .dll nativo
/// (Processing.NDI.Lib.x64.dll) debe estar junto al .exe (lo copia el .csproj de la App desde el NDI
/// Runtime instalado); si falta, o el CPU no es compatible, NDI queda NO DISPONIBLE sin tumbar la app.
/// Centraliza la inicialización (una sola vez), el descubrimiento de fuentes, la creación/destrucción de receptores y el
/// CIERRE del runtime al salir (<see cref="Shutdown"/>): sin él, un proceso que recibió NDI por RUDP no termina nunca
/// (ver <see cref="NdiLifetime"/>).
/// </summary>
public static class NdiRuntime
{
    private static readonly NdiLifetime _lifetime = new(() => NDIlib.initialize(), () => NDIlib.destroy());

    // Respaldo para las salidas que no pasan por el cierre de la app (pruebas, herramientas, un Main que simplemente vuelve):
    // ProcessExit corre ANTES de que ExitProcess termine los hilos, así que NDIlib_destroy aún puede pararlos.
    static NdiRuntime() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        try { _lifetime.Shutdown(TimeSpan.FromSeconds(2)); }
        catch { /* saliendo: nada que hacer */ }
    };

    /// <summary>True si el runtime NDI cargó e inicializó (se intenta una sola vez, de forma defensiva) y no se ha cerrado.</summary>
    public static bool IsAvailable => _lifetime.IsAvailable;

    /// <summary>Cerrar el runtime al salir (por defecto). false = como antes, sin NDIlib_destroy. (<c>Ndi:DestroyOnExit</c>.)</summary>
    public static bool DestroyOnExit
    {
        get => _lifetime.DestroyOnExit;
        set => _lifetime.DestroyOnExit = value;
    }

    /// <summary>Receptores y búsquedas NDI abiertos ahora mismo.</summary>
    public static int OpenCount => _lifetime.OpenCount;

    /// <summary>
    /// Crea un receptor NDI (recv_create_v3) llevando la cuenta de los abiertos. <see cref="IntPtr.Zero"/> si NDI no está
    /// disponible, ya se cerró o el SDK no pudo crearlo. Se libera con <see cref="DestroyReceiver"/>.
    /// </summary>
    public static IntPtr CreateReceiver(ref NDIlib.recv_create_v3_t settings)
    {
        if (!_lifetime.TryOpen()) return IntPtr.Zero;
        IntPtr recv = IntPtr.Zero;
        try { recv = NDIlib.recv_create_v3(ref settings); }
        finally { if (recv == IntPtr.Zero) _lifetime.Close(); }
        return recv;
    }

    /// <summary>Destruye un receptor creado con <see cref="CreateReceiver"/> (recv_destroy).</summary>
    public static void DestroyReceiver(IntPtr recv)
    {
        if (recv == IntPtr.Zero) return;
        try { NDIlib.recv_destroy(recv); }
        finally { _lifetime.Close(); }
    }

    /// <summary>
    /// Descubre las fuentes NDI visibles (nombres «MÁQUINA (Fuente)»), incluidas las locales. Devuelve vacío
    /// si NDI no está disponible. <paramref name="waitMs"/> da margen al descubrimiento mDNS.
    /// </summary>
    public static IReadOnlyList<string> DiscoverSources(int waitMs = 1500)
    {
        if (!_lifetime.TryOpen()) return Array.Empty<string>();
        try
        {
            using var finder = new Finder(showLocalSources: true, groups: null!, extraIps: null!);
            for (int slept = 0; finder.Sources.Count == 0 && slept < waitMs; slept += 200)
                Thread.Sleep(200);
            return finder.Sources
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
        finally { _lifetime.Close(); }
    }

    /// <summary>
    /// Cierra el runtime NDI (NDIlib_destroy: de milisegundos a ~2 s) si se cargó, esperando hasta <paramref name="waitForOpen"/> a que
    /// se cierren los receptores y búsquedas que sigan abiertos (<see cref="NdiShutdownResult.InUse"/> si no da tiempo).
    /// Llamarlo al salir, con los receptores ya cerrados; después NDI deja de estar disponible. Idempotente.
    /// </summary>
    public static NdiShutdownResult Shutdown(TimeSpan waitForOpen) => _lifetime.Shutdown(waitForOpen);
}
