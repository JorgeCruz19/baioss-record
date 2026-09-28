namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Decide cuándo una captura que YA entregaba frames se ha quedado muda. El vigilante del supervisor no lo ve: FFmpeg
/// sigue imprimiendo su progreso cada segundo con el contador de frames parado (medido con el FFmpeg empaquetado), así
/// que una tarjeta DeckLink que deja de entregar frames sin decir «No input signal detected» (cuelgue del driver, un
/// fallo del bus) dejaba el canal en «SEÑAL OK» con el preview congelado y el archivo sin crecer, indefinidamente.
/// Reglas: se mide por CONEXIÓN del proceso de captura al relé (un proceso nuevo empieza de cero); solo se juzga cuando
/// es juzgable (dispositivo abierto, la tarjeta sin decir «sin señal» y el proceso conectado al relé) y tras haber visto
/// frames en esa conexión; y una sola reapertura por episodio (si tras reabrir no vuelve a fluir nada, no insiste).
/// Pura y sin reloj propio, para probarla aparte.
/// </summary>
public sealed class FrameFlowWatch
{
    private long _key = long.MinValue;
    private long _frames;
    private DateTimeOffset _lastAdvance;
    private bool _flowed;

    /// <param name="key">Identifica el proceso y su conexión al relé (cambia con cada proceso o conexión nuevos).</param>
    /// <param name="frames">Frames de vídeo recibidos en esa conexión.</param>
    /// <param name="judgeable">Dispositivo abierto, sin «sin señal» de la tarjeta y proceso conectado al relé.</param>
    /// <param name="now">Instante de la medida.</param>
    /// <param name="timeout">Tiempo sin frames que se tolera.</param>
    public bool ShouldReopen(long key, long frames, bool judgeable, DateTimeOffset now, TimeSpan timeout)
    {
        if (key != _key) { _key = key; _frames = frames; _lastAdvance = now; _flowed = frames > 0; return false; }
        if (frames != _frames) { _frames = frames; _lastAdvance = now; _flowed = true; return false; }
        if (!judgeable || !_flowed) { _lastAdvance = now; return false; }
        if (now - _lastAdvance <= timeout) return false;
        _lastAdvance = now;
        _flowed = false; // una sola reapertura por episodio
        return true;
    }
}
