namespace Baioss.Record.Application.Channels;

/// <summary>Una muestra de la telemetría del proceso del canal (≈ 1 s, el ritmo de <c>-stats_period</c>).</summary>
/// <param name="DroppedFrames"><c>drop_frames</c> acumulado que imprime FFmpeg en el proceso actual.</param>
/// <param name="Frames"><c>frame</c> acumulado que imprime FFmpeg en el proceso actual. FFmpeg lo cuenta en la PRIMERA salida
/// de vídeo, que en el proceso del canal es el preview en passthrough: un frame por cada frame de ENTRADA (con una señal
/// entrelazada el preview saca uno por campo, y el parser del progreso lo vuelve a contar en frames de la fuente).</param>
/// <param name="MediaTimeUs">Tiempo de medios ya procesado (<c>out_time</c>, µs); ≤ 0 mientras FFmpeg no lo sabe.</param>
/// <param name="Elapsed">Reloj monotónico del llamador al recibir la muestra.</param>
public readonly record struct SaturationSample(long DroppedFrames, long Frames, long MediaTimeUs, TimeSpan Elapsed);

/// <summary>Qué síntoma de saturación trajo la última muestra.</summary>
public enum SaturationSymptom
{
    None,
    /// <summary>FFmpeg descartó más frames de los que explica la conversión de cadencia del preset.</summary>
    UnexplainedDrops,
    /// <summary>Con una fuente que entrega a ritmo de tiempo real, el proceso avanza de forma sostenida más despacio que el reloj.</summary>
    BehindRealTime,
}

/// <summary>
/// La regla de la alarma de FRAMES PERDIDOS («el equipo no da abasto»): decide, muestra a muestra, si el proceso del canal
/// da síntomas de saturación y les aplica la racha de <see cref="DropAlarmTracker"/> para no parpadear. Pura y testeable.
///
/// <para>Por qué no basta el contador <c>drop_frames</c> de FFmpeg (medido con el FFmpeg empaquetado, git-2026-06):</para>
/// <list type="bullet">
///   <item>Cuenta también los frames que FFmpeg tira A PROPÓSITO para convertir la cadencia que pide el preset (<c>-r</c>):
///   de 30 a 25 fps, 5 por segundo con la CPU sobrada (speed 20x). Eso no es saturación: se descuenta lo que la conversión
///   explica (<see cref="FrameRateConversion.DroppedFraction"/> por frame de entrada) más un margen para la fase.</item>
///   <item>FFmpeg 6.1 y posteriores lo informan SOLO de la primera salida de vídeo —en el canal, el preview, en passthrough, que
///   nunca descarta—; FFmpeg 6.0 y anteriores lo suman de TODAS (preview + grabación). El cliente aporta su propio FFmpeg: con
///   uno moderno el contador del canal es siempre 0 y con uno antiguo incluye la conversión de la grabación. La regla vale
///   para los dos.</item>
///   <item>Una saturación de verdad casi nunca lo sube: con una fuente que entrega a ritmo de tiempo real (tarjeta, archivo
///   leído con <c>-re</c>) el proceso simplemente se atrasa (speed 0,55x con 0 descartes); y con marcas de reloj de pared
///   (DirectShow) repite frames en vez de tirarlos. Por eso, con esas fuentes, el atraso SOSTENIDO también es síntoma:
///   la velocidad media (tiempo de medios ÷ tiempo de pared) de las últimas <see cref="SpeedWindow"/> muestras por debajo de
///   <see cref="BehindSpeed"/>. Las muestras congeladas (no llegó nada que procesar: la fuente calló, el disco no acepta
///   escrituras) no cuentan: eso no es lentitud y tiene sus propias alarmas. Con una entrada de red o NDI la velocidad no
///   dice nada del equipo (el emisor o la red pueden ir a trompicones) y no se mira.</item>
/// </list>
/// <para>Lo que el relé o el dispositivo DESCARTAN porque su cola se llenó es pérdida segura y no pasa por aquí: el motor
/// del canal lo trata aparte.</para>
/// </summary>
public sealed class SaturationDetector
{
    private readonly DropAlarmTracker _streak;

    public SaturationDetector(int onAfter = 3, int offAfter = 5) => _streak = new DropAlarmTracker(onAfter, offAfter);

    /// <summary>Margen (frames) que absorbe la fase de la conversión: los descartes llegan enteros y lo esperado es
    /// fraccionario (de 30 a 25 cae uno de cada seis frames: en un segundo pueden caer 4, 5 o 6).</summary>
    public double ConversionSlack { get; init; } = 2;

    /// <summary>Por debajo de esta fracción del tiempo real, de forma sostenida, el proceso no da abasto.</summary>
    public double BehindSpeed { get; init; } = 0.85;

    /// <summary>Cuántas muestras con avance promedia la velocidad. Un bache de uno o dos segundos que se recupera solo no
    /// debe llamarse saturación; ocho segundos por debajo de <see cref="BehindSpeed"/> ya son atraso de verdad.</summary>
    public int SpeedWindow { get; init; } = 8;

    /// <summary>Dos muestras separadas por menos que esto se miden juntas (FFmpeg soltó dos bloques de golpe).</summary>
    public TimeSpan MinSampleInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    // --- Descartes: los acumulados desde la última línea base frente a los que pide la conversión. ---
    private bool _haveBase;
    private long _baseDrops, _lastDrops, _lastFrames, _lastMedia = -1;
    private double _expectedSinceBase;
    private long _unexplainedAtBase;
    private long _unexplained;

    // --- Velocidad: ventana de las últimas muestras con avance (tiempo de medios, tiempo de pared). ---
    private bool _realTime;
    private bool _haveRef;
    private long _refMedia;
    private TimeSpan _refWall;
    private readonly Queue<(double Media, double Wall)> _window = new();
    private double _windowMedia, _windowWall;

    /// <summary>¿Debe estar encendida la alarma?</summary>
    public bool Active => _streak.Active;

    /// <summary>Frames perdidos en la grabación que la conversión de cadencia NO explica (acumulado de la sesión, a través de
    /// los procesos que la componen). Es lo que se le enseña al operador como «perdidos».</summary>
    public long UnexplainedDrops => Volatile.Read(ref _unexplained);

    /// <summary>Velocidad media de la ventana (fracción del tiempo real) o null si aún no se pudo medir. Solo con una fuente
    /// a ritmo de tiempo real.</summary>
    public double? Speed { get; private set; }

    /// <summary>El síntoma que trajo la última muestra (el que explica por qué se encendió la alarma).</summary>
    public SaturationSymptom LastSymptom { get; private set; }

    /// <summary>
    /// Empieza de cero para una grabación nueva.
    /// </summary>
    /// <param name="realTimeSource">La fuente entrega a ritmo constante de tiempo real y no se para por su cuenta
    /// (<c>ICaptureSource.DeliversInRealTime</c>): solo entonces el atraso del proceso es cosa del equipo.</param>
    public void Reset(bool realTimeSource)
    {
        _realTime = realTimeSource;
        _streak.Reset();
        _haveBase = false;
        _baseDrops = _lastDrops = _lastFrames = 0;
        _lastMedia = -1;
        _expectedSinceBase = 0;
        _unexplainedAtBase = 0;
        Volatile.Write(ref _unexplained, 0);
        _haveRef = false;
        ClearWindow();
        Speed = null;
        LastSymptom = SaturationSymptom.None;
    }

    /// <summary>Procesa una muestra; devuelve si la alarma debe estar activa.</summary>
    /// <param name="sample">La telemetría del proceso.</param>
    /// <param name="conversion">La conversión de cadencia vigente (la fuente puede redetectar su tasa a mitad).</param>
    public bool Update(SaturationSample sample, FrameRateConversion conversion)
    {
        // Proceso nuevo (pieza nueva tras una caída, carta de ajuste, codificador degradado): sus contadores y su tiempo de
        // medios empiezan de cero. Se toma línea base de nuevo; lo ya contado como perdido se conserva.
        bool restarted = _haveBase && (sample.Frames < _lastFrames || sample.DroppedFrames < _lastDrops ||
                                       (sample.MediaTimeUs > 0 && _lastMedia > 0 && sample.MediaTimeUs < _lastMedia));
        bool drops = UpdateDrops(sample, conversion, restarted);
        bool behind = _realTime && UpdateSpeed(sample, restarted);
        if (sample.MediaTimeUs > 0) _lastMedia = sample.MediaTimeUs;
        LastSymptom = drops ? SaturationSymptom.UnexplainedDrops
                    : behind ? SaturationSymptom.BehindRealTime
                    : SaturationSymptom.None;
        return _streak.Observe(drops || behind);
    }

    private bool UpdateDrops(SaturationSample s, FrameRateConversion conversion, bool restarted)
    {
        // Con una tasa de salida fija y la de la fuente desconocida no se sabe qué descartes pide la conversión: se mira sin
        // juzgar (línea base en cada muestra) antes que dar por pérdida lo que puede ser la conversión pedida.
        if (!_haveBase || restarted || conversion.IsUnknown) { Rebase(s); return false; }

        _expectedSinceBase += (s.Frames - _lastFrames) * conversion.DroppedFraction;
        _lastFrames = s.Frames;
        _lastDrops = s.DroppedFrames;
        // Sin conversión, cualquier descarte es pérdida (como siempre); con ella, lo que exceda lo pedido más el margen.
        double slack = conversion.DropsFrames ? ConversionSlack : 0;
        double excess = s.DroppedFrames - _baseDrops - _expectedSinceBase - slack;
        if (excess < 1) return false;
        long candidate = _unexplainedAtBase + (long)Math.Floor(excess);
        if (candidate <= _unexplained) return false;
        Volatile.Write(ref _unexplained, candidate);
        return true;
    }

    private void Rebase(SaturationSample s)
    {
        _haveBase = true;
        _baseDrops = _lastDrops = s.DroppedFrames;
        _lastFrames = s.Frames;
        _expectedSinceBase = 0;
        _unexplainedAtBase = _unexplained;
    }

    private bool UpdateSpeed(SaturationSample s, bool restarted)
    {
        if (restarted) { _haveRef = false; ClearWindow(); Speed = null; }
        if (s.MediaTimeUs <= 0) return BehindNow(); // FFmpeg aún no sabe por dónde va: nada nuevo que medir
        if (!_haveRef)
        {
            _haveRef = true; _refMedia = s.MediaTimeUs; _refWall = s.Elapsed;
            return false;
        }
        double wall = (s.Elapsed - _refWall).TotalSeconds;
        if (wall < MinSampleInterval.TotalSeconds) return BehindNow();
        double media = (s.MediaTimeUs - _refMedia) / 1_000_000.0;
        _refMedia = s.MediaTimeUs;
        _refWall = s.Elapsed;
        // Congelada: no llegó nada que procesar. No es lentitud (la fuente calló, el relé se reabre, el disco no acepta
        // escrituras: cada cosa con su alarma) y no entra en la media; el veredicto se queda como estaba.
        if (media <= 0) return BehindNow();

        _window.Enqueue((media, wall));
        _windowMedia += media;
        _windowWall += wall;
        while (_window.Count > SpeedWindow)
        {
            var old = _window.Dequeue();
            _windowMedia -= old.Media;
            _windowWall -= old.Wall;
        }
        Speed = _windowMedia / _windowWall;
        return BehindNow();
    }

    private bool BehindNow() => _window.Count >= SpeedWindow && _windowWall > 0 && _windowMedia / _windowWall < BehindSpeed;

    private void ClearWindow()
    {
        _window.Clear();
        _windowMedia = _windowWall = 0;
    }
}
