using System.Diagnostics;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>Cómo quedó el runtime NDI al pedir su cierre (<see cref="NdiRuntime.Shutdown"/>).</summary>
public enum NdiShutdownResult
{
    /// <summary>NDI no se llegó a cargar en este proceso: no hay nada que cerrar.</summary>
    NotLoaded,
    /// <summary>Runtime cerrado con NDIlib_destroy (en esta llamada o en una anterior).</summary>
    Destroyed,
    /// <summary>Cierre desactivado (<c>Ndi:DestroyOnExit = false</c>): el runtime se deja abierto, como antes.</summary>
    Disabled,
    /// <summary>Siguen abiertos receptores o búsquedas NDI: destruir el runtime bajo ellos no es seguro.</summary>
    InUse,
}

/// <summary>
/// Ciclo de vida del runtime NDI en el proceso: lo inicializa una sola vez, cuenta los objetos NDI abiertos (receptores y
/// búsquedas) y lo CIERRA (NDIlib_destroy) cuando ya no queda ninguno. Va aparte de <see cref="NdiRuntime"/>, que le pasa las
/// llamadas nativas, para poder probarlo sin la DLL.
///
/// <para>Por qué hay que cerrarlo: con RUDP —el transporte NDI por defecto— el runtime arranca hilos propios que ni
/// recv_destroy ni el paso del tiempo paran; solo NDIlib_destroy. Si el proceso sale sin él, ExitProcess termina esos hilos y
/// después la descarga de la DLL de NDI se queda esperándolos para siempre: el proceso no acaba y Windows ya no deja terminarlo
/// (ni con el Administrador de tareas) hasta reiniciar. Medido con NDI 6.2.1 y 6.3.2 el 2026-10-17; con TCP o UDP unicast no pasa.</para>
/// </summary>
internal sealed class NdiLifetime(Func<bool> initialize, Action destroy)
{
    private readonly object _gate = new();
    private bool _checked, _loaded, _destroyed;
    private int _open; // receptores y búsquedas NDI vivos

    /// <summary>false = no cerrar el runtime nunca (lo de antes). Lo fija la app con <c>Ndi:DestroyOnExit</c>.</summary>
    public bool DestroyOnExit { get; set; } = true;

    /// <summary>True si el runtime cargó e inicializó (se intenta una sola vez) y aún no se ha cerrado.</summary>
    public bool IsAvailable
    {
        get { lock (_gate) return EnsureLoaded() && !_destroyed; }
    }

    /// <summary>Receptores y búsquedas NDI abiertos ahora mismo.</summary>
    public int OpenCount
    {
        get { lock (_gate) return _open; }
    }

    /// <summary>Apunta un objeto NDI que se va a crear. false si NDI no está disponible o ya se cerró: entonces no se crea.</summary>
    public bool TryOpen()
    {
        lock (_gate)
        {
            if (!EnsureLoaded() || _destroyed) return false;
            _open++;
            return true;
        }
    }

    /// <summary>El objeto NDI apuntado con <see cref="TryOpen"/> ya se destruyó (o no se llegó a crear).</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_open > 0) _open--;
            Monitor.PulseAll(_gate); // despierta a un Shutdown que espera a que se cierren
        }
    }

    /// <summary>
    /// Cierra el runtime (NDIlib_destroy) si se cargó, esperando hasta <paramref name="waitForOpen"/> a que se cierren los objetos
    /// NDI que sigan abiertos. Después ya no se crea ninguno (<see cref="IsAvailable"/> pasa a false). Idempotente.
    /// </summary>
    public NdiShutdownResult Shutdown(TimeSpan waitForOpen)
    {
        lock (_gate)
        {
            if (_destroyed) return NdiShutdownResult.Destroyed;
            if (!_loaded) return NdiShutdownResult.NotLoaded; // sin cargar la DLL ahora, al salir
            if (!DestroyOnExit) return NdiShutdownResult.Disabled;
            long deadline = Stopwatch.GetTimestamp() + (long)(waitForOpen.TotalSeconds * Stopwatch.Frequency);
            while (_open > 0)
            {
                var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
                if (left <= TimeSpan.Zero) return NdiShutdownResult.InUse;
                Monitor.Wait(_gate, left);
            }
            _destroyed = true; // antes de destruir: a partir de aquí TryOpen ya no deja crear nada
            destroy();
            return NdiShutdownResult.Destroyed;
        }
    }

    private bool EnsureLoaded()
    {
        if (!_checked)
        {
            _checked = true;
            try { _loaded = initialize(); }
            catch { _loaded = false; } // DllNotFound (runtime ausente), BadImageFormat (x86), etc.
        }
        return _loaded;
    }
}
