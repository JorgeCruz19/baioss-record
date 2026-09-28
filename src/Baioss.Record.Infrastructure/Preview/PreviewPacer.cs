using System.Diagnostics;

namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Colchón de preview: absorbe una entrada que LLEGA A RÁFAGAS (un servidor RTMP que entrega a trompicones, una red
/// con jitter) para que el preview se vea a cadencia constante, como hace un reproductor (ffplay guarda una cola y
/// presenta cada frame a su hora). Los frames se encolan tal como llegan y se ENTREGAN a la cadencia nominal de la
/// fuente desde un hilo propio, con un retardo objetivo (<see cref="Depth"/>) que es el margen para absorber las
/// ráfagas. Solo afecta al preview: la grabación no pasa por aquí y conserva sus marcas de tiempo.
///
/// <list type="bullet">
///   <item><b>Sin colchón (Depth = 0):</b> paso directo, síncrono, en el hilo del lector: exactamente lo de siempre.</item>
///   <item><b>Arranque:</b> se entrega el primer frame cuando la cola alcanza el objetivo (o pasó ese tiempo desde el
///   primer frame, con una fuente más lenta de lo declarado).</item>
///   <item><b>Hueco en la llegada:</b> la cola se vacía y el preview mantiene el último frame (se cuenta en
///   <see cref="Repeated"/>); al volver el flujo se sigue de inmediato, sin volver a llenar el colchón: la ráfaga que
///   sigue a un hueco lo rellena sola.</item>
///   <item><b>Deriva:</b> si la cola se aleja del objetivo (por debajo de la mitad o por encima del doble: una fuente
///   que entrega algo más o menos de lo declarado, o que pierde frames), se corrige a razón de un frame repetido o
///   descartado cada <see cref="CorrectionPeriod"/> entregas (±20 % de velocidad como mucho): invisible en un monitor
///   de confianza y sin el efecto goma de variar la velocidad de forma continua. Dentro de la banda no se toca nada:
///   las ráfagas normales oscilan ahí.</item>
///   <item><b>Ráfaga mayor que el colchón:</b> la cola se acota (<see cref="MaxFrames"/>) descartando lo más viejo:
///   el preview salta hacia delante en vez de quedarse cada vez más atrás (y la memoria queda acotada).</item>
///   <item><b>Búferes:</b> reutilizados de un pool acotado, sin copias. Los 2 últimos entregados quedan en cuarentena
///   (la UI copia el frame a su textura en el Dispatcher, poco después de recibirlo): nunca se reescribe el búfer
///   que la UI está leyendo. Es exactamente el anillo de 3 que había antes (uno llenándose + dos entregados).</item>
/// </list>
/// </summary>
public sealed class PreviewPacer : IDisposable
{
    private const int QuarantineSize = 2;
    /// <summary>Fuera de la banda muerta, una corrección (frame repetido o descartado) cada tantas entregas.</summary>
    public const int CorrectionPeriod = 5;

    private readonly object _sync = new();
    private readonly int _frameBytes;
    private readonly Action<byte[]> _deliver;
    private readonly string _name;
    private readonly Queue<byte[]> _queue = new();
    private readonly Stack<byte[]> _free = new();
    private readonly Queue<byte[]> _quarantine = new();
    private int _allocated;
    private Thread? _thread;
    private bool _disposed;

    // Configuración (se puede cambiar en caliente: cada proceso nuevo del canal la vuelve a fijar).
    private double _intervalTicks;
    private int _target, _low, _high, _max;

    // Estado del ritmo.
    private bool _playing;
    private long _nextDue;
    private long _firstArrival;
    private int _arrivals;
    private long _ticks;

    public PreviewPacer(int frameBytes, Action<byte[]> deliver, string name = "preview")
    {
        _frameBytes = frameBytes;
        _deliver = deliver;
        _name = name;
        Configure(TimeSpan.Zero, 25);
    }

    /// <summary>Retardo objetivo del preview (0 = paso directo).</summary>
    public TimeSpan Depth { get; private set; }
    /// <summary>Cadencia de entrega (frames por segundo). Con 0 se mide de la llegada durante el llenado inicial.</summary>
    public double NominalFps { get; private set; }
    /// <summary>Frames en cola objetivo / tope de la cola (diagnóstico y tests).</summary>
    public int TargetFrames { get { lock (_sync) return _target; } }
    public int MaxFrames { get { lock (_sync) return _max; } }
    /// <summary>Frames en cola ahora mismo.</summary>
    public int QueuedFrames { get { lock (_sync) return _queue.Count; } }
    /// <summary>Entregados / repetidos (huecos y correcciones lentas) / descartados (ráfagas y correcciones rápidas).</summary>
    public long Delivered { get; private set; }
    public long Repeated { get; private set; }
    public long Dropped { get; private set; }

    /// <summary>Fija (o cambia en caliente) el retardo objetivo y la cadencia. Al pasar a 0 se vacía la cola.</summary>
    public void Configure(TimeSpan depth, double nominalFps)
    {
        lock (_sync)
        {
            Depth = depth < TimeSpan.Zero ? TimeSpan.Zero : depth;
            NominalFps = nominalFps > 0 ? nominalFps : 0;
            double fps = NominalFps > 0 ? NominalFps : 25;
            _intervalTicks = Stopwatch.Frequency / fps;
            _target = Depth == TimeSpan.Zero ? 0 : Math.Max(1, (int)Math.Round(fps * Depth.TotalSeconds));
            // Banda muerta ancha (la mitad y el doble del objetivo): las ráfagas normales oscilan dentro sin corrección.
            _low = Math.Max(1, _target / 2);
            _high = Math.Max(_target + 2, 2 * _target);
            _max = _high + Math.Max(3, (int)Math.Ceiling(fps / 2)); // más medio segundo de ráfaga por encima del doble
            if (Depth == TimeSpan.Zero)
            {
                while (_queue.Count > 0) _free.Push(_queue.Dequeue());
                _playing = false; _firstArrival = 0; _arrivals = 0;
            }
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Búfer para el siguiente frame que llega (del pool; nunca uno que la UI pueda estar leyendo).</summary>
    public byte[] Rent()
    {
        lock (_sync)
        {
            if (_free.Count > 0) return _free.Pop();
            // Tope del pool: la cola llena, la cuarentena, y un frame en vuelo por cada lector (dos durante un relevo).
            if (_allocated < _max + QuarantineSize + 2)
            {
                _allocated++;
                return new byte[_frameBytes];
            }
            // No debería pasar (Enqueue acota la cola): se sacrifica el frame más viejo de la cola.
            if (_queue.Count > 0) { Dropped++; return _queue.Dequeue(); }
            _allocated++;
            return new byte[_frameBytes];
        }
    }

    /// <summary>El frame no se muestra (el lector no tiene el mando): el búfer vuelve al pool.</summary>
    public void Discard(byte[] frame)
    {
        lock (_sync) _free.Push(frame);
    }

    /// <summary>Un frame completo acaba de llegar. Sin colchón se entrega aquí mismo; con colchón se encola.</summary>
    public void Enqueue(byte[] frame)
    {
        bool direct;
        lock (_sync)
        {
            if (_disposed) { _free.Push(frame); return; }
            direct = _target == 0;
            if (!direct)
            {
                long now = Stopwatch.GetTimestamp();
                if (_queue.Count >= _max) { _free.Push(_queue.Dequeue()); Dropped++; }
                _queue.Enqueue(frame);
                if (!_playing)
                {
                    if (_firstArrival == 0) _firstArrival = now;
                    _arrivals++;
                    double elapsed = Stopwatch.GetElapsedTime(_firstArrival, now).TotalSeconds;
                    if (_queue.Count >= _target || elapsed >= Depth.TotalSeconds)
                    {
                        // Cadencia desconocida: la de llegada durante el llenado (con ráfagas es una media razonable).
                        if (NominalFps <= 0 && _arrivals >= 5 && elapsed > 0.2)
                        {
                            NominalFps = _arrivals / elapsed;
                            _intervalTicks = Stopwatch.Frequency / NominalFps;
                        }
                        _playing = true;
                        _nextDue = now;
                    }
                }
                EnsureThread();
                Monitor.PulseAll(_sync);
            }
        }
        if (direct) DeliverAndQuarantine(frame);
    }

    /// <summary>Cadencia de entrega en vigor (diagnóstico y tests). Es la declarada: se probó seguir la tasa medida de llegada
    /// (ventanas de 10 s) y con un servidor que se para y luego descarga de golpe la medida oscilaba y arrastraba la
    /// cadencia (19–36 frames/s en pantalla); con la cadencia fija y la banda ancha salían 29–30 exactos.</summary>
    public double DeliveryFps { get { lock (_sync) return Stopwatch.Frequency / _intervalTicks; } }

    private void DeliverAndQuarantine(byte[] frame)
    {
        try { _deliver(frame); }
        finally
        {
            lock (_sync)
            {
                Delivered++;
                _quarantine.Enqueue(frame);
                while (_quarantine.Count > QuarantineSize) _free.Push(_quarantine.Dequeue());
            }
        }
    }

    private void EnsureThread()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"preview-pacer-{_name}" };
        _thread.Start();
    }

    private void Loop()
    {
        while (true)
        {
            byte[]? frame = null;
            lock (_sync)
            {
                while (!_disposed && (!_playing || _target == 0)) Monitor.Wait(_sync);
                if (_disposed) return;
                long now = Stopwatch.GetTimestamp();
                long wait = _nextDue - now;
                if (wait > 0)
                {
                    Monitor.Wait(_sync, TimeSpan.FromTicks(Math.Max(1, wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
                    continue;
                }
                // Tick de entrega.
                _ticks++;
                int depth = _queue.Count;
                if (depth == 0)
                {
                    Repeated++;                       // hueco: se mantiene el último frame
                    _nextDue = now + (long)_intervalTicks;
                    continue;
                }
                bool correctionTick = _ticks % CorrectionPeriod == 0;
                if (correctionTick && depth < _low)
                {
                    Repeated++;                       // vamos cortos: un frame repetido (más lento un 20 %)
                    _nextDue += (long)_intervalTicks;
                    continue;
                }
                if (correctionTick && depth > _high)
                {
                    _free.Push(_queue.Dequeue());     // vamos largos: un frame descartado (más rápido un 20 %)
                    Dropped++;
                }
                frame = _queue.Dequeue();
                // Si el hilo se retrasó más de un frame (máquina saturada), no se acumula deuda: seguiría a ráfagas.
                _nextDue = now - _nextDue > _intervalTicks ? now + (long)_intervalTicks : _nextDue + (long)_intervalTicks;
            }
            DeliverAndQuarantine(frame); // fuera del candado: el manejador copia el frame (UI, capturas) y puede tardar
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Clear();
            Monitor.PulseAll(_sync);
        }
    }
}
