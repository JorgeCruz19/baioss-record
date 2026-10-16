namespace Baioss.Record.Infrastructure.Preview;

/// <summary>Cómo se vio el preview de un canal durante un intervalo (lo que se escribe en el registro).</summary>
/// <param name="SourceFps">Frames por segundo que llegaron de la fuente.</param>
/// <param name="DisplayHz">Refrescos por segundo de la pantalla (los que pidió WPF).</param>
/// <param name="IrregularPercent">Cuadros enseñados que se vieron un número de refrescos distinto del que toca a esa cadencia
/// (en 60 Hz, un 29.97 se ve dos refrescos; un 25, dos o tres).</param>
/// <param name="MedianLeadMs">Retardo típico que añade el planificador (mediana del intervalo).</param>
/// <param name="MaxLeadMs">El mayor retardo del intervalo.</param>
/// <param name="Frames">Frames que llegaron en el intervalo.</param>
/// <param name="Late">Frames que llegaron tarde para su turno (cada uno se ve con una duración irregular).</param>
/// <param name="Skipped">Frames que no se llegaron a enseñar.</param>
public readonly record struct PreviewPresentationReport(
    double SourceFps, double DisplayHz, double IrregularPercent, double MedianLeadMs, double MaxLeadMs, int Frames, long Late, long Skipped);

/// <summary>
/// Cuenta, refresco a refresco, cómo se está viendo el preview: cadencia de la fuente, refrescos de la pantalla, cuadros con
/// una duración irregular y el retardo del planificador. Sirve para diagnosticar en el equipo del cliente un preview «a
/// tirones»: si la pantalla no llega a sus refrescos (WPF por debajo de 60 Hz, p. ej. en una pantalla virtual de acceso
/// remoto) o si los frames llegan tarde. Pura: el reloj lo pone el llamador (segundos), siempre desde el mismo hilo.
/// </summary>
public sealed class PreviewPresentationStats
{
    private double _start = double.NaN;
    private int _arrivals, _ticks, _ticksSinceShown;
    private bool _shownBefore;
    private readonly List<int> _holds = new();
    private readonly List<double> _leads = new();
    private long _lateBase, _skippedBase;

    /// <summary>Segundos desde el principio del intervalo actual.</summary>
    public double Elapsed(double now) => double.IsNaN(_start) ? 0 : now - _start;

    /// <summary>Llegó un frame, con este retardo hasta su turno.</summary>
    public void OnArrival(double now, double lead)
    {
        if (double.IsNaN(_start)) _start = now;
        _arrivals++;
        _leads.Add(lead);
    }

    /// <summary>Un refresco de pantalla; <paramref name="shown"/> si en él se enseñó un frame nuevo.</summary>
    public void OnTick(double now, bool shown)
    {
        if (double.IsNaN(_start)) _start = now;
        _ticks++;
        _ticksSinceShown++;
        if (!shown) return;
        if (_shownBefore) _holds.Add(_ticksSinceShown);
        _shownBefore = true;
        _ticksSinceShown = 0;
    }

    /// <summary>Cierra el intervalo y empieza otro. <paramref name="late"/> y <paramref name="skipped"/> son los contadores
    /// acumulados del planificador (se informa la diferencia con el intervalo anterior). Null si no llegó nada.</summary>
    public PreviewPresentationReport? Close(double now, long late, long skipped)
    {
        double seconds = Elapsed(now);
        PreviewPresentationReport? report = null;
        if (seconds > 0 && _arrivals > 1)
        {
            double fps = _arrivals / seconds, hz = _ticks / seconds;
            var leads = _leads.OrderBy(x => x).ToArray();
            report = new PreviewPresentationReport(fps, hz, IrregularPercent(_holds, hz / fps),
                leads[leads.Length / 2] * 1000, leads[^1] * 1000, _arrivals, late - _lateBase, skipped - _skippedBase);
        }
        _start = now;
        _arrivals = 0;
        _ticks = 0;
        _holds.Clear();
        _leads.Clear();
        _lateBase = late;
        _skippedBase = skipped;
        return report;
    }

    /// <summary>
    /// Porcentaje de cuadros con una duración (en refrescos) distinta de la que toca a <paramref name="refreshesPerFrame"/>:
    /// con una razón entera (60 Hz / 29.97 ≈ 2) solo vale esa; con una fraccionaria (60 / 25 = 2,4), sus dos vecinas; con una
    /// fuente más rápida que la pantalla, un refresco por cuadro.
    /// </summary>
    public static double IrregularPercent(IReadOnlyCollection<int> holds, double refreshesPerFrame)
    {
        if (holds.Count == 0) return 0;
        int low, high;
        if (refreshesPerFrame <= 1) low = high = 1;
        else if (Math.Abs(refreshesPerFrame - Math.Round(refreshesPerFrame)) <= 0.1) low = high = (int)Math.Round(refreshesPerFrame);
        else { low = (int)Math.Floor(refreshesPerFrame); high = low + 1; }
        return 100.0 * holds.Count(h => h < low || h > high) / holds.Count;
    }
}
