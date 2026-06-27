using NewTek;
using NewTek.NDI;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Acceso SEGURO al runtime NDI (SDK NewTek vía el paquete NDILibDotNetCoreBase). El .dll nativo
/// (Processing.NDI.Lib.x64.dll) debe estar junto al .exe (lo copia el .csproj de la App desde el NDI
/// Runtime instalado); si falta, o el CPU no es compatible, NDI queda NO DISPONIBLE sin tumbar la app.
/// Centraliza la inicialización (una sola vez) y el descubrimiento de fuentes.
/// </summary>
public static class NdiRuntime
{
    private static readonly object _gate = new();
    private static bool _checked;
    private static bool _available;

    /// <summary>True si el runtime NDI cargó e inicializó. Se intenta una sola vez, de forma defensiva.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                if (_checked) return _available;
                _checked = true;
                try { _available = NDIlib.initialize(); }
                catch { _available = false; } // DllNotFound (runtime ausente), BadImageFormat (x86), etc.
                return _available;
            }
        }
    }

    /// <summary>
    /// Descubre las fuentes NDI visibles (nombres «MÁQUINA (Fuente)»), incluidas las locales. Devuelve vacío
    /// si NDI no está disponible. <paramref name="waitMs"/> da margen al descubrimiento mDNS.
    /// </summary>
    public static IReadOnlyList<string> DiscoverSources(int waitMs = 1500)
    {
        if (!IsAvailable) return Array.Empty<string>();
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
    }
}
