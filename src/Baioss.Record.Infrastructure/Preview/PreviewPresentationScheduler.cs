namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Planificador de PRESENTACIÓN del preview: decide en qué refresco de pantalla se enseña cada frame para que el preview
/// avance a la cadencia de la fuente aunque los frames lleguen a trompicones.
///
/// <para>Por qué (medido con una DeckLink Duo 2 en 1080i59.94 el 2026-10-16): la tarjeta entrega un cuadro cada 33,4 ms
/// exactos, y un FFmpeg que la lee directamente los saca casi igual de regulares (desvío p99 de 2,5 ms); pero entre el
/// receptor permanente, el relé y el proceso del canal la llegada a la aplicación se desvía de esa rejilla 20–25 ms (p99),
/// y hasta 45 ms mientras se graba un preset que desentrelaza por campo. Pintar cada frame al llegar lo reparte mal entre
/// los refrescos de un monitor de 60 Hz: uno de cada cinco cuadros se veía uno o tres refrescos en vez de dos (tres de cada
/// cinco grabando a 59.94), un preview «a tirones» aunque llegaran todos los frames.</para>
///
/// <para>Cómo: cada frame recibe un TURNO en una rejilla regular (la cadencia media de llegada del tramo, que el jitter no
/// altera) con un margen de retardo, y en cada refresco se enseña el frame más reciente cuyo turno ya llegó. El margen se
/// adapta a cada equipo: un frame que llega tarde para su turno retrasa la rejilla lo justo (sin pasar de
/// <see cref="MaxLead"/>), y el margen que sobra se recorta poco a poco, adelantando cada turno como mucho
/// <see cref="MaxDecayPerFrame"/> (el preview va un 1–2 % más deprisa unos segundos, sin saltos). El retardo se queda así
/// justo por encima del jitter que hay.</para>
///
/// <para>Puro y sin hilos: el reloj (segundos, monotónico) lo pone el llamador. La vista lo alimenta desde el hilo del
/// lector (<see cref="Enqueue"/>) y lo consulta en cada refresco (<see cref="Take"/>), bajo su propio candado.</para>
/// </summary>
public sealed class PreviewPresentationScheduler<T> where T : class
{
    /// <summary>Un frame «a tiempo» llega al menos esto antes de su turno (holgura frente al momento del refresco).</summary>
    public const double OnTimeMargin = 0.004;

    /// <summary>Margen extra que se deja sin recortar (para no ir persiguiendo el jitter frame a frame).</summary>
    public const double DecaySlack = 0.004;

    /// <summary>Cada cuánto se mide el margen que sobró (el menor de la ventana).</summary>
    public const double DecayWindow = 1.0;

    /// <summary>Lo más que se adelanta un turno para recortar margen: 0,5 ms por frame (1,5 % a 30 fps).</summary>
    public const double MaxDecayPerFrame = 0.0005;

    /// <summary>Retardo máximo entre la llegada de un frame y su turno: con un atasco mayor, mejor un tirón que un preview
    /// cada vez más atrasado.</summary>
    public const double MaxLead = 0.250;

    /// <summary>Retardo con el que empieza un tramo (antes de saber cuánto jitter hay).</summary>
    public const double InitialLead = 0.040;

    /// <summary>Frames del tramo a partir de los que la cadencia se mide (antes se usa la del tramo anterior o 29.97).</summary>
    public const int MinFramesForRate = 8;

    /// <summary>Separación mínima entre turnos mientras se mide la cadencia: un refresco de 60 Hz. Dos turnos así de separados
    /// nunca caen en el mismo refresco, y una fuente de 59.94 (16,68 ms) no acumula retardo.</summary>
    private const double WarmupSpacing = 1.0 / 60;
    private const double DefaultInterval = 1001.0 / 30000;
    private const double MinInterval = 1.0 / 240;
    private const double MaxInterval = 1.0;

    private readonly Queue<(T Item, double Due)> _queue = new();
    private readonly int _capacity;
    private double _lastArrival = double.NaN;
    private double _segmentStart;      // llegada del primer frame del tramo (para medir la cadencia)
    private long _segmentFrames;       // frames del tramo después del primero
    private double _interval = DefaultInterval;
    private double _lastDue;
    private double _windowStart;
    private double _windowMinLead = double.MaxValue;
    private int _windowFrames;
    private double _decayPerFrame;

    /// <param name="capacity">Frames en cola como mucho (si nadie los consume, p. ej. con la ventana minimizada): los más
    /// viejos salen sin enseñarse.</param>
    public PreviewPresentationScheduler(int capacity = 16) => _capacity = Math.Max(2, capacity);

    /// <summary>Cadencia medida de la fuente (segundos entre frames).</summary>
    public double FrameInterval => _interval;

    /// <summary>Con cuánta antelación a su turno llegó el último frame (≈ el retardo que añade el planificador).</summary>
    public double LastLead { get; private set; } = InitialLead;

    /// <summary>Frames en cola esperando su turno.</summary>
    public int Count => _queue.Count;

    /// <summary>Veces que un frame llegó tarde para su turno y la rejilla se retrasó (diagnóstico).</summary>
    public long LatePushes { get; private set; }

    /// <summary>Frames que salieron sin enseñarse: dos turnos en un mismo refresco, o cola llena (diagnóstico).</summary>
    public long Skipped { get; private set; }

    /// <summary>
    /// Llega un frame en <paramref name="arrival"/> (segundos). Se le asigna su turno; si la cola rebosa, los más viejos van a
    /// <paramref name="discarded"/> (sus búferes se pueden reutilizar).
    /// </summary>
    public void Enqueue(T item, double arrival, ICollection<T> discarded)
    {
        double due;
        if (double.IsNaN(_lastArrival) || arrival < _lastArrival || arrival - _lastArrival > ResetGap())
        {
            // Tramo nuevo (el primero, o la fuente se paró: otro modo, señal perdida, carta de ajuste…): rejilla nueva, con
            // la cadencia del tramo anterior hasta medir la de este.
            _segmentStart = arrival;
            _segmentFrames = 0;
            due = arrival + InitialLead;
            _windowStart = arrival;
            _windowMinLead = double.MaxValue;
            _windowFrames = 0;
            _decayPerFrame = 0;
        }
        else if (++_segmentFrames < MinFramesForRate)
        {
            // Primeros frames del tramo: aún no se sabe la cadencia, así que cada uno sale con el retardo inicial respecto a su
            // llegada (suponer una cadencia aquí disparaba el retardo con una fuente más rápida), y nunca a menos de un
            // refresco del anterior: dos frames que llegan juntos tendrían el mismo turno y uno no se vería.
            due = Math.Max(arrival + InitialLead, _lastDue + WarmupSpacing);
        }
        else
        {
            // La cadencia es la media desde el principio del tramo: el jitter de cada llegada se diluye entre todas (±20 ms
            // en 30 frames ya es ±1 ms) y la deriva entre el reloj de la fuente y el del PC queda incluida.
            _interval = Math.Clamp((arrival - _segmentStart) / _segmentFrames, MinInterval, MaxInterval);
            // Turno siguiente de la rejilla, adelantado un poco si en la ventana anterior sobró margen.
            due = _lastDue + _interval - _decayPerFrame;
            // Tope del retardo: MaxLead, y que lo que espera quepa en la cola (a 120 fps, 14 frames son 117 ms).
            double maxLead = Math.Min(MaxLead, (_capacity - 2) * _interval);
            if (due - arrival < OnTimeMargin)
            {
                // Llegó tarde para su turno: la rejilla entera se retrasa lo justo para que este llegue a tiempo, y ese margen
                // queda para los siguientes (un frame no se puede enseñar antes de que llegue).
                LatePushes++;
                due = arrival + OnTimeMargin;
            }
            else if (due - arrival > maxLead)
            {
                // Demasiado pronto (tras un atasco largo, o la cadencia medida se quedó corta): se adelanta hasta el tope.
                due = arrival + maxLead;
            }
        }

        double lead = due - arrival;
        LastLead = lead;
        _windowMinLead = Math.Min(_windowMinLead, lead);
        _windowFrames++;
        if (arrival - _windowStart >= DecayWindow)
        {
            // El margen que sobró en toda la ventana (el menor) se recorta a la mitad en la siguiente, repartido entre sus
            // frames y sin pasar de MaxDecayPerFrame: el retardo baja cuando el jitter baja, sin que se note.
            double spare = _windowMinLead - OnTimeMargin - DecaySlack;
            _decayPerFrame = spare > 0 ? Math.Min(MaxDecayPerFrame, spare / 2 / Math.Max(1, _windowFrames)) : 0;
            _windowStart = arrival;
            _windowMinLead = double.MaxValue;
            _windowFrames = 0;
        }

        _queue.Enqueue((item, due));
        _lastDue = due;
        _lastArrival = arrival;
        while (_queue.Count > _capacity)
        {
            discarded.Add(_queue.Dequeue().Item);
            Skipped++;
        }
    }

    /// <summary>Turnos vencidos que pueden esperar a los refrescos siguientes antes de saltarse los más viejos: tres son
    /// 100 ms a 30 fps, lo que tarda una pantalla en saltarse varios refrescos seguidos y recuperarlos con los que sí llegan.</summary>
    public const int MaxDueBacklog = 3;

    /// <summary>
    /// Refresco de pantalla en <paramref name="now"/> (segundos): el frame que toca enseñar (o null: se mantiene el que se
    /// ve). Si la pantalla se saltó refrescos y vencieron dos turnos, se enseña el más viejo y el otro sale en el refresco
    /// siguiente: con una pantalla que no llega a sus refrescos (WPF a 45 Hz en una pantalla virtual de acceso remoto) se ven
    /// todos los frames, un refresco tarde, en vez de perder uno. Solo si se acumulan más de <see cref="MaxDueBacklog"/>
    /// (una fuente más rápida que la pantalla) se saltan los más viejos, que van a <paramref name="discarded"/>.
    /// </summary>
    public T? Take(double now, ICollection<T> discarded)
    {
        int due = 0;
        foreach (var (_, d) in _queue)
        {
            if (d > now) break;
            due++;
        }
        if (due == 0) return null;
        for (; due > MaxDueBacklog; due--)
        {
            discarded.Add(_queue.Dequeue().Item);
            Skipped++;
        }
        return _queue.Dequeue().Item;
    }

    /// <summary>Vacía la cola (la vista se desengancha de la fuente): todo va a <paramref name="discarded"/>.</summary>
    public void Clear(ICollection<T> discarded)
    {
        while (_queue.Count > 0) discarded.Add(_queue.Dequeue().Item);
        _lastArrival = double.NaN;
    }

    /// <summary>Hueco a partir del que la fuente se considera parada: medio segundo, o cuatro frames si es más lenta.</summary>
    private double ResetGap() => Math.Max(0.5, 4 * _interval);
}
