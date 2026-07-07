namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Detecta un ESTANCAMIENTO del archivo de grabación: aunque FFmpeg siga reportando progreso por stdout, si los
/// bytes en disco no CRECEN durante un margen, la grabación está de facto muerta (encoder colgado emitiendo
/// frames vacíos, escritura bloqueada, ruta de red que descarta en silencio). Complementa al watchdog de
/// progreso, que no vería este caso. Estado mínimo y lógica pura (testeable sin proceso ni disco). Una lectura
/// de bytes NEGATIVA significa «no evaluable ahora» (p. ej. pausa / sin sonda) y reinicia el reloj. (Auditoría #55.)
/// </summary>
internal sealed class FileGrowthTracker
{
    private long _lastBytes = -1;
    private DateTimeOffset _lastGrowthUtc;

    public FileGrowthTracker(DateTimeOffset startedUtc) => _lastGrowthUtc = startedUtc;

    /// <summary>
    /// Registra la medida de bytes actual y devuelve <c>true</c> si el archivo lleva SIN crecer más de
    /// <paramref name="timeout"/> (estancado). <paramref name="currentBytes"/> &lt; 0 = no evaluable → reinicia el reloj.
    /// </summary>
    public bool IsStalled(long currentBytes, DateTimeOffset now, TimeSpan timeout)
    {
        if (currentBytes < 0) { _lastGrowthUtc = now; return false; }                 // no evaluable (pausa / sin sonda)
        if (currentBytes > _lastBytes) { _lastBytes = currentBytes; _lastGrowthUtc = now; return false; } // creció
        return _lastBytes >= 0 && now - _lastGrowthUtc > timeout;                     // sin crecer más allá del margen
    }
}
