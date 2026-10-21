using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// El ritmo del hilo de un emisor NDI de prueba, en su propio reloj (<see cref="Now"/>: segundos desde que se creó): cuándo se
/// paró, cuánto y dónde —dentro de qué llamada al SDK de NDI, en pausas del recolector de .NET o fuera de los dos—, para que una
/// prueba espere a que vaya a su hora antes de medir, descarte solo la medida en la que cae un parón y diga qué lo causó. Un emisor a
/// su ritmo (<see cref="Period"/>) que se para manda después de golpe los periodos atrasados, y hasta que se pone al día sus marcas no
/// están a su hora; uno al ritmo del SDK (<c>clock_video</c>, sin <see cref="Period"/>) no envía mientras la llamada no vuelve. Un
/// parón se ve también mientras dura (una llamada que no vuelve, un periodo que no sale), no solo cuando el emisor vuelve.
/// <para>En el PC de pruebas, del 18 al 21-oct-2026, el emisor de NdiSparseRecordingTests y de NdiSyntheticSourceTests se paraba
/// ~2,48 s (2462–2486 ms) 2–3 veces por pasada de las dos clases, también con la CPU libre, y la guarda de entonces (el retraso
/// máximo de toda la prueba, descubrimiento y apertura incluidos) lo achacaba a la carga del equipo. Todo pasó en un mismo arranque
/// de Windows de 4 días; tras reiniciar el equipo no se ha vuelto a ver, ni en las pruebas ni con una sonda aparte, así que no se
/// sabe dónde se paraba: si vuelve, la salida de la prueba lo dice. Lo único del proceso que espera 2,5 s es el plugin NDI HX de las
/// NDI 6 Tools (instaladas el 17-oct; el SDK lo carga en todo proceso NDI desde %ProgramData%\NDI\ndi-plugins.v1.json): prueba
/// conexiones TCP a equipos de la red, 2,5 s cada una si no contestan. Sospechoso, no demostrado.</para>
/// </summary>
internal sealed class NdiSenderPace
{
    /// <summary>Retraso (o llamada al SDK) a partir del cual hay un parón. El emisor duerme con el temporizador de Windows (sale hasta
    /// ~16 ms tarde) y una llamada al SDK tarda unos ms (con <c>clock_video</c>, hasta un cuadro: 33 ms a 29.97).</summary>
    public const double StallMs = 100;
    /// <summary>Por debajo de este retraso el emisor vuelve a ir a su hora: ya mandó lo atrasado.</summary>
    private const double OnTimeMs = 25;

    /// <summary>Un parón: de cuándo a cuándo (s del reloj del emisor; hasta que volvió a ir a su hora, o ahora si sigue), lo más tarde
    /// que llegó a ir y dónde se paró.</summary>
    public sealed record Stall(double From, double To, double LateMs, string Where)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{LateMs:0} ms {Where}, de {From:0.00} a {To:0.00} s del emisor");
    }

    private readonly bool _paced;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<Stall> _stalls = new();
    // Emisor a su ritmo: el parón en curso (desde cuándo, lo más tarde y dónde), cuándo vence el siguiente periodo y lo que pasó en su
    // hilo desde el último periodo que salió a su hora (la llamada al SDK más lenta y las pausas del recolector): ahí se fue el tiempo.
    private double _behindFrom = double.NaN, _behindLateMs;
    private string _behindWhere = "";
    private double _nextDue = double.NaN;
    private string? _slowestCall;
    private double _slowestCallMs;
    private TimeSpan _gcPausedAtOnTime = GC.GetTotalPauseDuration();
    // La llamada al SDK en curso (o, sin ninguna, cuándo acabó la última) y las pausas del recolector hasta entonces.
    private string? _callName;
    private double _callStart = double.NaN, _lastCallEnd = double.NaN;
    private TimeSpan _gcPausedAtCallEnd;
    private bool _stopped;

    /// <param name="paced">true si el emisor marca su ritmo durmiendo hasta cada periodo y lo dice con <see cref="Period"/>; false si
    /// lo marca el SDK (<c>clock_video</c>): entonces el parón es una llamada que tarda o el hilo que tarda en volver a llamar.</param>
    public NdiSenderPace(bool paced) => _paced = paced;

    public double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>El emisor dejó de enviar (se cierra): lo que no envíe desde ahora ya no es un parón.</summary>
    public void Stop()
    {
        lock (_gate) _stopped = true;
    }

    /// <summary>
    /// Emisor a su ritmo: va a enviar el periodo que vencía a los <paramref name="due"/> s de su reloj; el siguiente vence a los
    /// <paramref name="nextDue"/>. Más de <see cref="StallMs"/> tarde empieza (o sigue) un parón, que acaba cuando un periodo vuelve a
    /// salir a su hora.
    /// </summary>
    public void Period(double due, double nextDue)
    {
        double now = Now, lateMs = (now - due) * 1000;
        lock (_gate)
        {
            _nextDue = nextDue;
            if (lateMs > StallMs)
            {
                if (double.IsNaN(_behindFrom)) { _behindFrom = due; _behindWhere = WhereLate(lateMs); }
                _behindLateMs = Math.Max(_behindLateMs, lateMs);
            }
            else if (lateMs < OnTimeMs)
            {
                if (!double.IsNaN(_behindFrom)) _stalls.Add(new Stall(_behindFrom, now, _behindLateMs, _behindWhere));
                _behindFrom = double.NaN;
                _behindLateMs = 0;
                _slowestCall = null;
                _slowestCallMs = 0;
                _gcPausedAtOnTime = GC.GetTotalPauseDuration();
            }
        }
    }

    /// <summary>Cronometra una llamada al SDK de NDI del hilo del emisor: <c>using (pace.Call("NDIlib_send_send_video_v2")) …</c>.</summary>
    public CallTimer Call(string name)
    {
        double start = Now;
        var gcPaused = GC.GetTotalPauseDuration();
        lock (_gate)
        {
            // Sin Period, el hilo que tarda en volver a llamar también es un parón (fuera del SDK).
            if (!_paced && !double.IsNaN(_lastCallEnd) && (start - _lastCallEnd) * 1000 > StallMs)
                _stalls.Add(OutsideCalls(_lastCallEnd, start, (gcPaused - _gcPausedAtCallEnd).TotalMilliseconds));
            _callName = name;
            _callStart = start;
        }
        return new CallTimer(this, name, start, gcPaused);
    }

    public readonly struct CallTimer : IDisposable
    {
        private readonly NdiSenderPace _pace;
        private readonly string _name;
        private readonly double _start;
        private readonly TimeSpan _gcPaused;

        internal CallTimer(NdiSenderPace pace, string name, double start, TimeSpan gcPaused)
        {
            _pace = pace; _name = name; _start = start; _gcPaused = gcPaused;
        }

        public void Dispose() => _pace.Ended(_name, _start, _gcPaused);
    }

    private void Ended(string name, double start, TimeSpan gcPausedBefore)
    {
        double end = Now, ms = (end - start) * 1000;
        var gcPaused = GC.GetTotalPauseDuration();
        lock (_gate)
        {
            _callName = null;
            _callStart = double.NaN;
            _lastCallEnd = end;
            _gcPausedAtCallEnd = gcPaused;
            if (ms <= StallMs) return;
            if (_paced)
            {
                if (ms > _slowestCallMs) { _slowestCall = name; _slowestCallMs = ms; }
                return;
            }
            double gcMs = (gcPaused - gcPausedBefore).TotalMilliseconds;
            _stalls.Add(new Stall(start, end, ms, gcMs >= ms / 2 ? InGc(gcMs) : InSdk(name, ms)));
        }
    }

    /// <summary>Dónde se fue el tiempo de un periodo que salió <paramref name="lateMs"/> tarde: en pausas del recolector (que paran
    /// todos los hilos de .NET, también al volver de una llamada nativa), dentro de una llamada al SDK o fuera de los dos (el hilo no
    /// corrió a tiempo). Bajo <see cref="_gate"/>.</summary>
    private string WhereLate(double lateMs)
    {
        double gcMs = (GC.GetTotalPauseDuration() - _gcPausedAtOnTime).TotalMilliseconds;
        if (gcMs >= lateMs / 2) return InGc(gcMs);
        if (_slowestCall is not null && _slowestCallMs >= lateMs / 2) return InSdk(_slowestCall, _slowestCallMs);
        return NotRunning(gcMs);
    }

    private Stall OutsideCalls(double from, double to, double gcMs)
    {
        double ms = (to - from) * 1000;
        return new Stall(from, to, ms, gcMs >= ms / 2 ? InGc(gcMs) : NotRunning(gcMs));
    }

    private static string InSdk(string call, double ms) => string.Create(CultureInfo.InvariantCulture,
        $"dentro de {call}: el SDK de NDI retuvo la llamada {ms:0} ms");

    private static string InGc(double ms) => string.Create(CultureInfo.InvariantCulture, $"en pausas del recolector de .NET ({ms:0} ms)");

    private static string NotRunning(double gcMs) => string.Create(CultureInfo.InvariantCulture,
        $"fuera del SDK de NDI y sin pausa del recolector ({gcMs:0} ms): el hilo del emisor no corrió a tiempo (equipo cargado)");

    /// <summary>
    /// Un parón que aún no ha cerrado ningún periodo ni ninguna llamada: el emisor sigue dentro de una llamada al SDK que no vuelve, su
    /// siguiente periodo ya debía haber salido o (sin <see cref="Period"/>) tarda en volver a llamar. Bajo <see cref="_gate"/>.
    /// </summary>
    private Stall? Ongoing(double now)
    {
        double since = _paced ? _nextDue : double.IsNaN(_callStart) ? _lastCallEnd : _callStart;
        double ms = (now - since) * 1000;
        if (_stopped || double.IsNaN(since) || ms <= StallMs) return null;
        string where = _callName is not null ? $"dentro de {_callName}, que aún no ha vuelto" : "fuera del SDK de NDI, aún sin volver a enviar";
        return new Stall(since, now, ms, where);
    }

    /// <summary>Los parones que se solapan con [<paramref name="from"/>, <paramref name="to"/>] (s del reloj del emisor), incluido el
    /// que siga en curso.</summary>
    public IReadOnlyList<Stall> Between(double from, double to)
    {
        double now = Now;
        lock (_gate)
        {
            var stalls = _stalls.Where(s => s.From <= to && s.To >= from).ToList();
            var ongoing = double.IsNaN(_behindFrom) ? Ongoing(now) : new Stall(_behindFrom, now, _behindLateMs, _behindWhere);
            if (ongoing is not null && ongoing.From <= to) stalls.Add(ongoing);
            return stalls;
        }
    }

    /// <summary>Todos los parones hasta ahora, para la salida de la prueba («a su hora» si no hubo).</summary>
    public string Summary()
    {
        var stalls = Between(0, double.MaxValue);
        return stalls.Count == 0 ? "a su hora" : "parado " + string.Join("; parado ", stalls);
    }

    /// <summary>Espera a que el emisor lleve <paramref name="onTime"/> sin parones (como mucho <paramref name="timeout"/>).</summary>
    public async Task WaitOnTimeAsync(TimeSpan onTime, TimeSpan timeout)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < timeout && Between(Now - onTime.TotalSeconds, Now).Count > 0) await Task.Delay(100);
    }

    /// <summary>
    /// Omite la prueba si el emisor se paró mientras grababa (de <paramref name="from"/> a <paramref name="to"/>, s de su reloj; desde
    /// un segundo antes del Grabar, porque lo que manda de golpe al ponerse al día también cae en el archivo): lo grabado no sirve
    /// para medir, ni para bien ni para mal. El mensaje dice dónde se paró.
    /// </summary>
    public void SkipIfStalledWhileRecording(double from, double to)
    {
        var stalls = Between(from, to);
        Skip.If(stalls.Count > 0, $"El emisor de la prueba se paró durante la grabación ({string.Join("; ", stalls)}); mientras no " +
                                  "fue a su hora, lo grabado no sirve para medir: repite la prueba.");
    }
}
