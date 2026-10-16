using System.Globalization;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Planificador de presentación del preview, con un reloj simulado (sin esperas reales): una pantalla de 60 Hz pide en cada
/// refresco el frame que toca. Con llegadas regulares cada frame se ve dos refrescos; con el jitter MEDIDO en una DeckLink
/// por el relé (solo preview, y grabando un preset que desentrelaza por campo) también, mientras que pintar cada frame al
/// llegar —lo de antes— dejaba uno de cada cinco (o tres de cada cinco) con una duración irregular. El retardo se adapta al
/// jitter, vuelve a bajar tras un atasco, una pausa de la fuente empieza un tramo nuevo y la cola no crece sin consumidor.
/// </summary>
public class PreviewPresentationSchedulerTests
{
    private const double Ntsc = 1001.0 / 30000;
    private readonly ITestOutputHelper _out;
    public PreviewPresentationSchedulerTests(ITestOutputHelper output) => _out = output;

    private sealed record Frame(int Index, double Arrival);

    private sealed record Run(List<(int Tick, Frame Frame)> Shown, List<Frame> Discarded, PreviewPresentationScheduler<Frame> Scheduler,
        List<(double Arrival, double Lead)> Leads, double TickHz)
    {
        /// <summary>Refrescos que se vio cada frame enseñado desde <paramref name="after"/> (el último no cuenta).</summary>
        public int[] Holds(double after = 0) =>
            Shown.Zip(Shown.Skip(1), (a, b) => (a, b)).Where(p => p.a.Tick / TickHz >= after).Select(p => p.b.Tick - p.a.Tick).ToArray();
    }

    /// <summary>Una pantalla a <paramref name="tickHz"/>: en cada refresco entran las llegadas ya ocurridas y se pide el frame que toca.</summary>
    private static Run Simulate(IReadOnlyList<double> arrivals, double tickHz = 60, double phase = 0.0037, bool consume = true,
        Func<int, bool>? tickHappens = null)
    {
        var s = new PreviewPresentationScheduler<Frame>();
        var discarded = new List<Frame>();
        var shown = new List<(int, Frame)>();
        var leads = new List<(double, double)>();
        int next = 0;
        for (int k = 0; ; k++)
        {
            double now = phase + k / tickHz;
            if (now > arrivals[^1] + 0.5) break;
            while (next < arrivals.Count && arrivals[next] <= now)
            {
                s.Enqueue(new Frame(next, arrivals[next]), arrivals[next], discarded);
                leads.Add((arrivals[next], s.LastLead));
                next++;
            }
            if (!consume || tickHappens?.Invoke(k) == false) continue;
            if (s.Take(now, discarded) is { } f) shown.Add((k, f));
        }
        return new Run(shown, discarded, s, leads, tickHz);
    }

    /// <summary>Lo de antes: cada frame se pinta en el primer refresco tras llegar (el último que llegó, si llegan dos).</summary>
    private static int[] HoldsPaintingOnArrival(IReadOnlyList<double> arrivals, double tickHz = 60, double phase = 0.0037, double after = 0)
    {
        var shownTicks = new List<int>();
        int next = 0, lastShownIndex = -1;
        for (int k = 0; ; k++)
        {
            double now = phase + k / tickHz;
            if (now > arrivals[^1] + 0.5) break;
            int newest = -1;
            while (next < arrivals.Count && arrivals[next] <= now) newest = next++;
            if (newest > lastShownIndex) { lastShownIndex = newest; if (now >= after) shownTicks.Add(k); }
        }
        return shownTicks.Zip(shownTicks.Skip(1), (a, b) => b - a).ToArray();
    }

    private static double[] Regular(double interval, double seconds, double start = 0.010) =>
        Enumerable.Range(0, (int)(seconds / interval)).Select(i => start + i * interval).ToArray();

    private static double[] Trace(string name) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "preview-arrivals", name))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => 0.010 + double.Parse(l, CultureInfo.InvariantCulture) / 1000)
            .ToArray();

    private static double Percent(int[] holds, int expected) => holds.Length == 0 ? 0 : 100.0 * holds.Count(h => h != expected) / holds.Length;

    private void Report(string what, int[] holds, int expected) =>
        _out.WriteLine(FormattableString.Invariant($"{what}: {Percent(holds, expected):0.0} % de {holds.Length} cuadros con duración irregular · ") +
                       string.Join(" ", holds.GroupBy(h => h).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));

    [Fact]
    public void RegularArrivals_AreEachShownOnce_ForTwoRefreshes_AtSixtyHz()
    {
        var run = Simulate(Regular(Ntsc, 30));
        var all = run.Holds();
        var steady = run.Holds(after: 8);
        Report("29.97 regular", all, 2);

        Assert.Equal(Regular(Ntsc, 30).Length, run.Shown.Count); // todos, una vez cada uno y en orden
        Assert.Equal(Enumerable.Range(0, run.Shown.Count), run.Shown.Select(s => s.Frame.Index));
        Assert.Empty(run.Discarded);
        // 29.97 en 60 Hz: dos refrescos por cuadro y, una vez cada 33 s, tres (la diferencia entre 29.97 y 30).
        Assert.All(steady, h => Assert.InRange(h, 2, 3));
        Assert.True(steady.Count(h => h == 3) <= 1, string.Join(",", steady.Where(h => h != 2)));
        // Bajar del retardo inicial (40 ms) al mínimo acorta uno o dos cuadros en un refresco: es lo que cuesta, y ya.
        Assert.True(all.Count(h => h != 2) <= 3, string.Join(",", all.Where(h => h != 2)));
        Assert.True(run.Leads[^1].Lead <= 0.015, $"sin jitter, el retardo debe bajar casi al mínimo: {run.Leads[^1].Lead * 1000:0} ms");
    }

    [Theory]
    [InlineData("decklink-relay-preview.txt", 5.0)]                     // medido: 17,7 % pintando al llegar
    [InlineData("decklink-relay-recording-nvenc-59.94.txt", 10.0)]      // medido: 62 % pintando al llegar
    public void MeasuredArrivals_FromADeckLinkThroughTheRelay_AreShownAtTheSourceCadence(string trace, double minImprovement)
    {
        var arrivals = Trace(trace);
        var run = Simulate(arrivals);
        var holds = run.Holds(after: 3);
        var before = HoldsPaintingOnArrival(arrivals, after: 3);
        Report("pintando al llegar", before, 2);
        Report("con el planificador", holds, 2);
        var steadyLeads = run.Leads.Where(l => l.Arrival >= 3).Select(l => l.Lead).OrderBy(x => x).ToArray();
        _out.WriteLine(FormattableString.Invariant($"retardo: mediana {steadyLeads[steadyLeads.Length / 2] * 1000:0} ms, máx {steadyLeads[^1] * 1000:0} ms; tarde {run.Scheduler.LatePushes}, sin enseñar {run.Scheduler.Skipped}"));

        Assert.Equal(arrivals.Length, run.Shown.Count + run.Discarded.Count);
        Assert.True(run.Discarded.Count <= 2, $"frames sin enseñar: {run.Discarded.Count}");
        Assert.True(Percent(holds, 2) <= 2.5, $"irregulares con el planificador: {Percent(holds, 2):0.0} %");
        Assert.True(Percent(before, 2) >= minImprovement * Math.Max(Percent(holds, 2), 0.5),
            $"antes {Percent(before, 2):0.0} %, ahora {Percent(holds, 2):0.0} %");
        Assert.True(steadyLeads[^1] < 0.120, $"retardo máximo {steadyLeads[^1] * 1000:0} ms");
    }

    [Fact]
    public void SyntheticJitter_IsAbsorbed_WithALeadJustAboveIt()
    {
        // Llegadas que se retrasan de 0 a 25 ms al azar (el jitter del relé) y, cada 47 frames, 20 ms más; siempre en orden,
        // como por un socket (un frame retrasado retrasa al que viene detrás).
        var rnd = new Random(42);
        var arrivals = Regular(Ntsc, 40).Select((t, i) => t + rnd.NextDouble() * 0.025 + (i % 47 == 0 ? 0.020 : 0)).ToArray();
        for (int i = 1; i < arrivals.Length; i++) arrivals[i] = Math.Max(arrivals[i], arrivals[i - 1] + 0.001);
        var run = Simulate(arrivals);
        var holds = run.Holds(after: 5);
        Report("jitter de 0-45 ms", holds, 2);
        var leads = run.Leads.Where(l => l.Arrival >= 5).Select(l => l.Lead).ToArray();

        Assert.True(Percent(holds, 2) <= 2.0, $"{Percent(holds, 2):0.0} %");
        Assert.Empty(run.Discarded);
        // El retardo cubre el jitter (hasta 45 ms) y poco más: ni se queda corto ni se dispara.
        Assert.InRange(leads.Max(), 0.030, 0.090);
    }

    [Fact]
    public void FramesArrivingInPairs_AreSpacedOut()
    {
        // Como el proceso del canal grabando a 59.94: dos frames juntos y luego un hueco del doble.
        var arrivals = Regular(Ntsc, 30).Select((t, i) => i % 2 == 0 ? t + Ntsc : t + 0.002).ToArray();
        var run = Simulate(arrivals);
        var holds = run.Holds(after: 3);
        Report("a pares", holds, 2);
        Assert.True(Percent(holds, 2) <= 1.0, $"{Percent(holds, 2):0.0} %");
        Assert.Empty(run.Discarded);
        Assert.True(Percent(HoldsPaintingOnArrival(arrivals, after: 3), 2) >= 40, "el caso de prueba debería ser malo pintando al llegar");
    }

    [Fact]
    public void A25fpsSource_OnSixtyHz_AlternatesTwoAndThreeRefreshes_WithoutOnesOrFours()
    {
        var run = Simulate(Regular(0.040, 30));
        var holds = run.Holds(after: 1);
        Report("25 fps", holds, 2);
        Assert.All(holds, h => Assert.InRange(h, 2, 3));
        Assert.InRange(holds.Average(), 2.38, 2.42); // 60 / 25 = 2,4: el reparto fijo de siempre, no al azar
        Assert.Empty(run.Discarded);
    }

    [Fact]
    public void A5994Source_OnSixtyHz_ShowsEveryFrameOnce()
    {
        var run = Simulate(Regular(1001.0 / 60000, 30));
        var steady = run.Holds(after: 8);
        Report("59.94 fps", run.Holds(), 1);
        // A 59.94 en 60 Hz no hay holgura: bajar del retardo inicial (40 ms) al mínimo solo se puede saltando un frame por
        // cada 16,7 ms que se recortan. Dos, al principio.
        Assert.True(run.Discarded.Count <= 2, $"frames sin enseñar: {run.Discarded.Count}");
        Assert.All(run.Discarded, f => Assert.True(f.Arrival < 8, "solo al principio"));
        Assert.All(steady, h => Assert.InRange(h, 1, 2));
        Assert.True(steady.Count(h => h == 2) <= 2, $"{steady.Count(h => h == 2)} cuadros de dos refrescos");
    }

    [Fact]
    public void AfterAStall_TheLeadComesBackDown()
    {
        // 10 s regulares, un atasco de 200 ms (llegan de golpe al final) y otros 20 s regulares.
        var arrivals = Regular(Ntsc, 30).Select(t => t is >= 10 and < 10.2 ? 10.2 + (t - 10) / 50 : t).ToArray();
        var run = Simulate(arrivals);
        double peak = run.Leads.Where(l => l.Arrival is >= 10 and < 11).Max(l => l.Lead);
        double after = run.Leads.Where(l => l.Arrival >= 28).Max(l => l.Lead);
        _out.WriteLine(FormattableString.Invariant($"retardo tras el atasco {peak * 1000:0} ms → a los 18 s {after * 1000:0} ms"));
        Assert.True(peak >= 0.150, $"el atasco debería haberse absorbido retrasando la rejilla ({peak * 1000:0} ms)");
        Assert.True(after <= 0.030, $"el retardo no volvió a bajar: {after * 1000:0} ms");
        Assert.True(run.Discarded.Count <= 1, $"frames sin enseñar: {run.Discarded.Count}");
    }

    [Fact]
    public void AfterTheSourcePauses_ANewSegmentStarts_AtTheInitialLead()
    {
        var arrivals = Regular(Ntsc, 5).Concat(Regular(Ntsc, 5, start: 7.0)).ToArray();
        var run = Simulate(arrivals);
        var first = run.Shown.First(s => s.Frame.Arrival >= 7.0);
        double shownAt = 0.0037 + first.Tick / 60.0;
        Assert.InRange(shownAt - first.Frame.Arrival, 0, PreviewPresentationScheduler<Frame>.InitialLead + 1 / 60.0 + 1e-9);
        Assert.Empty(run.Discarded);
        var second = run.Holds(after: 7.1);
        Assert.All(second, h => Assert.InRange(h, 1, 3));
        Assert.True(second.Count(h => h != 2) <= 3, string.Join(",", second.Where(h => h != 2)));
    }

    [Fact]
    public void WithoutAConsumer_TheQueueIsBounded_AndTheOldestComeBack()
    {
        var run = Simulate(Regular(Ntsc, 10), consume: false);
        Assert.Equal(16, run.Scheduler.Count);
        Assert.Equal(Regular(Ntsc, 10).Length - 16, run.Discarded.Count);
        Assert.Equal(Enumerable.Range(0, run.Discarded.Count), run.Discarded.Select(f => f.Index)); // los más viejos primero
    }

    [Fact]
    public void ADisplayThatMissesRefreshes_StillShowsEveryFrame_ARefreshLateAtMost()
    {
        // Como WPF en la pantalla virtual de acceso remoto del equipo de pruebas (41–53 Hz de 60): uno de cada cuatro refrescos
        // no llega. Con dos turnos vencidos a la vez se enseña el más viejo y el otro en el refresco siguiente: no se pierde
        // ninguno (antes, el más viejo se saltaba: 15–100 frames cada 10 s en ese equipo).
        var rnd = new Random(7);
        var alive = Enumerable.Range(0, 60 * 40).Select(_ => rnd.NextDouble() >= 0.25).ToArray();
        var arrivals = Regular(Ntsc, 30);
        var run = Simulate(arrivals, tickHappens: k => k < alive.Length && alive[k]);
        _out.WriteLine($"sin enseñar: {run.Discarded.Count} de {arrivals.Length}");
        // Solo se pierde alguno si la pantalla se salta cuatro refrescos seguidos (0,4 % de las veces aquí).
        Assert.True(run.Discarded.Count <= 2, $"sin enseñar: {run.Discarded.Count}");
        Assert.Equal(arrivals.Length, run.Shown.Count + run.Discarded.Count);
        Assert.True(run.Shown.Zip(run.Shown.Skip(1), (a, b) => b.Frame.Index > a.Frame.Index).All(x => x), "siempre hacia delante");
    }

    [Fact]
    public void FasterThanTheDisplay_OneFramePerRefresh_AndTheRestComeBack()
    {
        var arrivals = Regular(1.0 / 120, 10);
        var run = Simulate(arrivals);
        Assert.Equal(arrivals.Length, run.Shown.Count + run.Discarded.Count + run.Scheduler.Count);
        Assert.InRange(run.Shown.Count, arrivals.Length * 0.45, arrivals.Length * 0.55); // uno por refresco
        Assert.True(run.Scheduler.Count <= 3);
        Assert.True(run.Shown.Zip(run.Shown.Skip(1), (a, b) => b.Frame.Index > a.Frame.Index).All(x => x), "siempre hacia delante");
    }
}
