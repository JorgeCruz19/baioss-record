namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Silencio para el audio de una fuente NDI que no lo trae (salidas de grafismo, muchas cámaras PTZ) o que deja de traerlo.
/// El proceso del canal lee el audio NDI por una entrada <c>f32le</c> aparte y, sin datos en ella, FFmpeg no arranca —abre
/// las entradas en serie y se queda esperando la del audio—: ni preview ni grabación (medido con una salida NDI de grafismo
/// y con un emisor sintético mudo). Cuando pasan <see cref="Gap"/> sin audio mientras llega vídeo, cada PERIODO de la cadencia
/// declarada del vídeo (<see cref="NdiFrameCadence"/>) lleva su duración de silencio —tenga cuadro propio o lo rellene FFmpeg
/// repitiendo el anterior—, empezando por los periodos que ya pasaron sin audio: así el contador de muestras, que es como FFmpeg
/// pone las marcas a esta entrada (muestra/frecuencia), sigue a las marcas del vídeo (periodo/cadencia), y vídeo y audio siguen
/// alineados. El audio real, cuando llega, manda. Puro: el reloj lo pone el llamador, siempre desde el mismo hilo.
/// </summary>
internal sealed class NdiAudioGapFiller
{
    private DateTimeOffset? _lastAudio;
    private DateTimeOffset? _firstVideo;
    private long _periodsWithoutAudio;
    // Fracción de muestra pendiente, en 1/frameRateN de muestra (1601,6 por periodo a 29.97 y 48 kHz): con enteros el total
    // de muestras es exacto para cualquier número de periodos.
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
        _periodsWithoutAudio = 0;
        _remainder = 0;
        Filling = false;
    }

    /// <summary>Pasaron <paramref name="periods"/> periodos del vídeo (el de un cuadro real y los que se repiten tras él); devuelve
    /// las muestras de silencio que hay que enviar por ellos (0 si no toca).</summary>
    public int OnPeriods(DateTimeOffset now, long periods, int sampleRate, int frameRateN, int frameRateD)
    {
        if (periods <= 0) return 0;
        _firstVideo ??= now;
        _periodsWithoutAudio += periods;
        long count = periods;
        if (!Filling)
        {
            var since = _lastAudio is { } audio && audio > _firstVideo.Value ? audio : _firstVideo.Value;
            if (now - since < Gap) return 0;
            Filling = true;
            count = _periodsWithoutAudio; // los periodos que ya pasaron sin audio, y estos
        }
        if (sampleRate <= 0 || frameRateN <= 0 || frameRateD <= 0) return 0;
        if (frameRateN != _remainderRateN) { _remainder = 0; _remainderRateN = frameRateN; }
        long numerator = count * sampleRate * (long)frameRateD + _remainder;
        _remainder = numerator % frameRateN;
        return (int)Math.Min(numerator / frameRateN, int.MaxValue);
    }
}
