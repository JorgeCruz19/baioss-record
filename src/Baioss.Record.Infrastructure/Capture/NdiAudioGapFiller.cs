namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Silencio para el audio de una fuente NDI que no lo trae (salidas de grafismo, muchas cámaras PTZ) o que deja de traerlo.
/// El proceso del canal lee el audio NDI por una entrada <c>f32le</c> aparte y, sin datos en ella, FFmpeg no arranca —abre
/// las entradas en serie y se queda esperando la del audio—: ni preview ni grabación (medido con una salida NDI de grafismo
/// y con un emisor sintético mudo). Cuando pasan <see cref="Gap"/> sin audio mientras llega vídeo, cada cuadro de vídeo lleva
/// su duración de silencio, empezando por los cuadros que ya pasaron sin audio: así el contador de muestras sigue al de
/// cuadros, que es como FFmpeg pone las marcas de tiempo a estas entradas (vídeo n/fps, audio muestra/frecuencia), y vídeo y
/// audio siguen alineados. El audio real, cuando llega, manda. Puro: el reloj lo pone el llamador, siempre desde el mismo hilo.
/// </summary>
internal sealed class NdiAudioGapFiller
{
    private DateTimeOffset? _lastAudio;
    private DateTimeOffset? _firstVideo;
    private long _framesWithoutAudio;
    // Fracción de muestra pendiente, en 1/frameRateN de muestra (1601,6 por cuadro a 29.97 y 48 kHz): con enteros el total
    // de muestras es exacto para cualquier número de cuadros.
    private long _remainder;
    private int _remainderRateN;

    /// <summary>Sin audio durante este tiempo (con vídeo llegando) ⇒ la fuente no lo trae. Los bloques de audio NDI llegan
    /// cada ~20 ms: medio segundo sin ninguno ya no es un retraso.</summary>
    public TimeSpan Gap { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>¿Está enviando silencio ahora mismo?</summary>
    public bool Filling { get; private set; }

    /// <summary>Llegó un bloque de audio real: deja de rellenar.</summary>
    public void OnAudio(DateTimeOffset now)
    {
        _lastAudio = now;
        _framesWithoutAudio = 0;
        _remainder = 0;
        Filling = false;
    }

    /// <summary>Llegó un cuadro de vídeo; devuelve las muestras de silencio que hay que enviar tras él (0 si no toca).</summary>
    public int OnVideo(DateTimeOffset now, int sampleRate, int frameRateN, int frameRateD)
    {
        _firstVideo ??= now;
        _framesWithoutAudio++;
        long frames = 1;
        if (!Filling)
        {
            var since = _lastAudio is { } audio && audio > _firstVideo.Value ? audio : _firstVideo.Value;
            if (now - since < Gap) return 0;
            Filling = true;
            frames = _framesWithoutAudio; // los cuadros que ya pasaron sin audio, y este
        }
        if (sampleRate <= 0 || frameRateN <= 0 || frameRateD <= 0) return 0;
        if (frameRateN != _remainderRateN) { _remainder = 0; _remainderRateN = frameRateN; }
        long numerator = frames * sampleRate * (long)frameRateD + _remainder;
        _remainder = numerator % frameRateN;
        return (int)Math.Min(numerator / frameRateN, int.MaxValue);
    }
}
