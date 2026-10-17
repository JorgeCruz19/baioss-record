using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Cadencia del vídeo NDI que se sirve a FFmpeg: una fuente que entrega menos cuadros de los que declara se sirve a la cadencia
/// declarada repitiendo el último (FFmpeg pone las marcas por contador: sin esto la grabación salía comprimida en el tiempo), y una
/// que va a su ritmo pasa tal cual, sin repeticiones ni descartes, aunque su reloj derive o la red la entregue con vaivén.
/// </summary>
public sealed class NdiFrameCadenceTests
{
    private const int N2997 = 30000, D2997 = 1001;
    private static readonly double P2997 = (double)D2997 / N2997; // s

    /// <summary>
    /// Banco: el bucle del receptor con una fuente simulada. Cada cuadro real llega a su hora (nuestro reloj) con su marca (el reloj
    /// de la fuente); entre medias, el bucle pregunta por las repeticiones cada <c>tick</c> (o antes, si vencen antes: como la
    /// espera del receptor). Cuenta lo servido y cuándo.
    /// </summary>
    private sealed class Bench
    {
        private readonly int _n, _d;
        private readonly TimeSpan _tick;
        private TimeSpan _now;
        public NdiFrameCadence Cadence { get; }
        public long Served { get; private set; }
        public long RealServed { get; private set; }
        public long RealDropped { get; private set; }
        public TimeSpan LastServedAt { get; private set; }
        public TimeSpan MaxGapBetweenServed { get; private set; }
        /// <summary>Lo más que lo servido fue por detrás / por delante de nuestro reloj (en cuadros, desde el primer cuadro real).</summary>
        public double MaxLag { get; private set; }
        public double MaxLead { get; private set; }
        private TimeSpan? _first;

        public Bench(int n, int d, TimeSpan? tick = null, NdiFrameCadence? cadence = null)
        {
            _n = n; _d = d;
            _tick = tick ?? TimeSpan.FromMilliseconds(2);
            Cadence = cadence ?? new NdiFrameCadence();
        }

        /// <summary>Avanza el reloj hasta <paramref name="until"/> sirviendo las repeticiones que vayan venciendo.</summary>
        public void RunUntil(TimeSpan until)
        {
            while (_now < until)
            {
                var wait = Cadence.UntilNextRepeat(_now) is { } u && u > TimeSpan.Zero && u < _tick ? u : _tick;
                _now = _now + wait > until ? until : _now + wait;
                int due = Cadence.Due(_now);
                for (int i = 0; i < due; i++) Serve();
                Measure();
            }
        }

        private void Measure()
        {
            if (_first is not { } first) return;
            double expected = (_now - first).Ticks / ((double)TimeSpan.TicksPerSecond * _d / _n) + 1; // periodos empezados
            MaxLag = Math.Max(MaxLag, expected - Served);
            MaxLead = Math.Max(MaxLead, Served - expected);
        }

        /// <summary>Llega un cuadro real a <paramref name="arrival"/> con su marca <paramref name="source"/>.</summary>
        public NdiFrameCadence.Decision Frame(TimeSpan arrival, TimeSpan? source)
        {
            RunUntil(arrival);
            var decision = Cadence.OnFrame(_now, source, _n, _d);
            _first ??= _now;
            for (int i = 0; i < decision.RepeatsBefore; i++) Serve();
            if (decision.Serve) { Serve(); RealServed++; }
            else RealDropped++;
            Measure();
            return decision;
        }

        private void Serve()
        {
            if (Served > 0 && _now - LastServedAt > MaxGapBetweenServed) MaxGapBetweenServed = _now - LastServedAt;
            Served++;
            LastServedAt = _now;
        }
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    /// <summary>Base de la marca de la fuente: otro reloj, sin relación con el nuestro.</summary>
    private static readonly TimeSpan SourceEpoch = TimeSpan.FromDays(20000);

    [Fact]
    public void ASourceAtItsRate_IsServedFrameForFrame_EvenWithNetworkJitter()
    {
        // 10 minutos a 29.97 con hasta ±30 % de periodo de vaivén en la llegada (OBS por TCP: ±10 %): ni una repetición ni un
        // descarte, como antes.
        var rng = new Random(1);
        var bench = new Bench(N2997, D2997);
        int frames = (int)(600 / P2997);
        TimeSpan last = TimeSpan.Zero;
        for (int k = 0; k < frames; k++)
        {
            var arrival = S(k * P2997 + (rng.NextDouble() - 0.5) * 0.6 * P2997 + 0.05);
            if (arrival < last) arrival = last; // la red entrega en orden
            last = arrival;
            bench.Frame(arrival, SourceEpoch + S(k * P2997));
        }
        Assert.Equal(0, bench.Cadence.Repeated);
        Assert.Equal(0, bench.Cadence.Dropped);
        Assert.Equal(frames, bench.Served);
    }

    [Theory]
    [InlineData(300)]   // la fuente va un 0,03 % más despacio que nuestro reloj
    [InlineData(-300)]  // y más deprisa
    [InlineData(100)]
    public void ASourceAtItsRate_WithADriftingClock_GetsNoRepeatsInAnHour(int ppm)
    {
        // Su cadencia real es la declarada en SU reloj, que deriva respecto al nuestro (y respecto a sus propias marcas, como un
        // conversor SDI→NDI): sin cuadrar cada cuadro contra nuestro reloj, ni por las marcas, no sale una repetición en una hora.
        double period = P2997 * (1 + ppm * 1e-6);
        var bench = new Bench(N2997, D2997, TimeSpan.FromMilliseconds(5));
        int frames = (int)(3600 / P2997);
        for (int k = 0; k < frames; k++)
            bench.Frame(S(k * period + 0.02), SourceEpoch + S(k * period));
        Assert.Equal(0, bench.Cadence.Repeated);
        Assert.Equal(0, bench.Cadence.Dropped);
        Assert.Equal(frames, bench.Served);
    }

    [Fact]
    public void ASourceAtHalfTheDeclaredRate_IsServedAtTheDeclaredCadence_InRealTime()
    {
        // 29.97 declarados, uno de cada dos cuadros enviado: lo servido sigue al reloj (un cuadro por periodo), con las repeticiones
        // a su hora y todos los cuadros reales servidos.
        var bench = new Bench(N2997, D2997);
        int sent = 0;
        for (int k = 0; k < 1800; k += 2, sent++) // un minuto
            bench.Frame(S(k * P2997 + 0.03), SourceEpoch + S(k * P2997));
        bench.RunUntil(S(1800 * P2997 + 0.03));
        Assert.InRange(bench.Served, 1799, 1800);       // un cuadro por periodo (el último periodo puede estar aún por vencer)
        Assert.Equal(sent, bench.RealServed);           // y todos los reales
        Assert.Equal(0, bench.Cadence.Dropped);
        Assert.True(bench.MaxGapBetweenServed <= S(2 * P2997 + 0.002), $"Hueco máximo entre cuadros servidos: {bench.MaxGapBetweenServed.TotalMilliseconds:0.0} ms.");
        Assert.True(bench.MaxLag <= 2.01 && bench.MaxLead <= 0.01, $"Por detrás {bench.MaxLag:0.00}, por delante {bench.MaxLead:0.00} cuadros.");
    }

    [Fact]
    public void AGraphicsOutputThatSendsOnlyOnChange_IsFilledAtItsDeclaredRate_WithoutAccumulatingError()
    {
        // Una salida de grafismo que declara 60 y manda un cuadro cada 150–450 ms, fuera de la rejilla de 60 (medido: cada 12,1–
        // 25,7 periodos). Diez minutos después lo servido es exactamente el tiempo por 60, y nunca pasan más de dos periodos sin
        // cuadro.
        var rng = new Random(7);
        var bench = new Bench(60, 1);
        for (double t = 0; t < 600; t += 0.15 + rng.NextDouble() * 0.3)
            bench.Frame(S(t + 0.004 + rng.NextDouble() * 0.008), SourceEpoch + S(t));
        bench.RunUntil(S(600));
        // Lo servido cubre del primer cuadro (t=0) a ahora: 600 s × 60, menos lo que aún no venció (el periodo de gracia).
        Assert.InRange(bench.Served, 600 * 60 - 3, 600 * 60 + 1);
        Assert.Equal(0, bench.Cadence.Dropped);
        // Y en todo momento a la par del reloj: como mucho los dos periodos de la gracia por detrás (más el vaivén de la llegada,
        // hasta medio periodo a 60) y uno por delante.
        Assert.True(bench.MaxLag <= 3, $"Por detrás del reloj hasta {bench.MaxLag:0.00} cuadros.");
        Assert.True(bench.MaxLead <= 1.5, $"Por delante del reloj hasta {bench.MaxLead:0.00} cuadros.");
    }

    [Fact]
    public void AGappySource_DeliveredWithNetworkJitter_IsCountedByItsOwnClock()
    {
        // Como vMix: en la rejilla de 29.97 pero saltándose 1–6 cuadros, y la red los entrega con un retraso variable como el medido
        // (p50 ~20 ms, p99 ~80 ms, alguno de más de 100). Lo servido sigue a los periodos de la FUENTE —no a la llegada—: tras cada
        // cuadro real, exactamente sus periodos desde el primero, más un adelanto acotado; y los cuadros reales se sirven.
        var rng = new Random(3);
        var bench = new Bench(N2997, D2997);
        long slot = 0, realFrames = 0, worstAhead = 0;
        TimeSpan last = TimeSpan.Zero;
        while (slot < 18000) // diez minutos
        {
            double sourceT = slot * P2997;
            var arrival = S(sourceT + 0.01 - 0.015 * Math.Log(1 - rng.NextDouble()));
            if (arrival < last) arrival = last; // en orden
            last = arrival;
            bench.Frame(arrival, SourceEpoch + S(sourceT));
            realFrames++;
            long ahead = bench.Served - (slot + 1);
            Assert.True(ahead >= 0, $"Faltan cuadros servidos en el periodo {slot}: {ahead}.");
            worstAhead = Math.Max(worstAhead, ahead);
            slot += 2 + rng.Next(6); // se salta 1–6
        }
        Assert.InRange(worstAhead, 0, 2);
        Assert.True(bench.RealServed >= realFrames * 0.99, $"Reales servidos: {bench.RealServed} de {realFrames}.");
    }

    [Fact]
    public void AFrameLateInTransit_IsServed_AndTheAdvanceIsCorrectedWithinTheHold()
    {
        // Una fuente a su ritmo con un cuadro que llega 4 periodos tarde (y los de detrás, de golpe): se repite mientras tanto,
        // el retrasado se sirve igual y el adelanto que deja (no hay huecos que lo absorban) se corrige en segundos.
        var bench = new Bench(N2997, D2997);
        int frames = 900; // 30 s
        for (int k = 0; k < frames; k++)
        {
            double sourceT = k * P2997;
            double arrival = sourceT + 0.02;
            if (k is >= 100 and < 104) arrival = 104 * P2997 + 0.02; // 100–103 llegan juntos, con el 104
            bench.Frame(S(arrival), SourceEpoch + S(sourceT));
        }
        // Tras el último cuadro, lo servido vuelve a ser exactamente un cuadro por periodo de la fuente: lo repetido mientras
        // tanto se compensó descartando otros tantos.
        Assert.Equal(frames, bench.Served);
        Assert.InRange(bench.Cadence.Repeated, 3, 4);
        Assert.Equal(bench.Cadence.Repeated, bench.Cadence.Dropped);
    }

    [Fact]
    public void ASourceThatSendsMoreThanItDeclares_IsNotServedFaster()
    {
        // Declara 29.97 y manda el doble: sobran los que caen en el periodo de otro, sin ir por delante ni un cuadro.
        var bench = new Bench(N2997, D2997);
        for (int k = 0; k < 1200; k++)
            bench.Frame(S(k * P2997 / 2 + 0.02), SourceEpoch + S(k * P2997 / 2));
        Assert.InRange(bench.Served, 600, 601); // los periodos que duran: del 0 al 599 o 600 (el último cae en la mitad)
        Assert.True(bench.MaxLead <= 1, $"Por delante del reloj hasta {bench.MaxLead:0.00} cuadros.");
        Assert.Equal(0, bench.Cadence.Repeated);
    }

    [Fact]
    public void AStaleFirstFrame_OrAJumpInTheSourceClock_DoesNotInventABurstOfRepeats()
    {
        // OBS por TCP: el primer cuadro de una conexión trae una marca de hace 2,5 s y el siguiente llega 6 ms después; y a mitad
        // de camino el reloj del emisor salta +10 s (un ajuste de hora). En ambos casos cuenta la llegada.
        var bench = new Bench(N2997, D2997);
        bench.Frame(S(0), SourceEpoch);
        var second = bench.Frame(S(0.006), SourceEpoch + S(2.478));
        Assert.Equal(0, second.RepeatsBefore);
        for (int k = 1; k < 300; k++) bench.Frame(S(0.006 + k * P2997), SourceEpoch + S(2.478 + k * P2997));
        var jump = bench.Frame(S(0.006 + 300 * P2997), SourceEpoch + S(12.478 + 300 * P2997));
        Assert.Equal(0, jump.RepeatsBefore);
        for (int k = 301; k < 600; k++) bench.Frame(S(0.006 + k * P2997), SourceEpoch + S(12.478 + k * P2997));
        Assert.Equal(0, bench.Cadence.Repeated);
        Assert.InRange(bench.Served, 599, 601);
    }

    [Fact]
    public void WithoutSourceTimestamps_TheArrivalCounts()
    {
        var bench = new Bench(N2997, D2997);
        for (int k = 0; k < 900; k += 3) bench.Frame(S(k * P2997 + 0.01), null);
        Assert.InRange(bench.Served, 897, 898);
        Assert.Equal(0, bench.Cadence.Dropped);
    }

    [Fact]
    public void TheRepeatWaitsForTheGrace_AndUntilNextRepeatSaysWhen()
    {
        var cadence = new NdiFrameCadence();
        var t0 = TimeSpan.FromSeconds(5);
        Assert.Null(cadence.UntilNextRepeat(t0));
        Assert.Equal(0, cadence.Due(t0)); // sin cuadro, nada que repetir
        Assert.Equal(new NdiFrameCadence.Decision(0, true), cadence.OnFrame(t0, SourceEpoch, N2997, D2997));
        // El siguiente cuadro tocaba un periodo después: se repite el último cuando va otro periodo tarde.
        var until = cadence.UntilNextRepeat(t0)!.Value;
        Assert.Equal(2 * P2997 * 1000, until.TotalMilliseconds, 0.001);
        Assert.Equal(0, cadence.Due(t0 + until - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1, cadence.Due(t0 + until));
        Assert.Equal(0, cadence.Due(t0 + until));                          // ya servida
        Assert.Equal(P2997 * 1000, cadence.UntilNextRepeat(t0 + until)!.Value.TotalMilliseconds, 0.001);
        Assert.Equal(2, cadence.Due(t0 + until + S(2 * P2997 + 1e-6)));   // un bucle que se durmió: las dos que vencieron
    }

    [Fact]
    public void AfterReset_NothingIsRepeated_AndTheNextFrameStartsAfresh()
    {
        var cadence = new NdiFrameCadence();
        cadence.OnFrame(TimeSpan.Zero, SourceEpoch, N2997, D2997);
        cadence.Reset();
        Assert.Equal(0, cadence.Due(TimeSpan.FromSeconds(2)));
        Assert.Null(cadence.UntilNextRepeat(TimeSpan.FromSeconds(2)));
        // El cuadro que vuelve, tres segundos después, no arrastra 90 repeticiones.
        Assert.Equal(new NdiFrameCadence.Decision(0, true), cadence.OnFrame(TimeSpan.FromSeconds(3), SourceEpoch + TimeSpan.FromSeconds(3), N2997, D2997));
    }

    [Fact]
    public void ARateChange_StartsAfresh()
    {
        var bench = new Bench(N2997, D2997);
        var cadence = bench.Cadence;
        cadence.OnFrame(TimeSpan.Zero, SourceEpoch, N2997, D2997);
        var decision = cadence.OnFrame(S(0.5), SourceEpoch + S(0.5), 60, 1);
        Assert.Equal(new NdiFrameCadence.Decision(0, true), decision);
    }

    [Fact]
    public void ACoarseTimer_DelaysTheRepeats_ButNotTheCount()
    {
        // El bucle del receptor puede despertar tarde (temporizador de Windows a 15,6 ms): a 60 cuadros declarados y la mitad
        // enviados, las repeticiones salen a veces de dos en dos, pero la cuenta sigue al reloj.
        var bench = new Bench(60, 1, TimeSpan.FromMilliseconds(15.6));
        for (int k = 0; k < 1200; k += 2) bench.Frame(S(k / 60.0 + 0.01), SourceEpoch + S(k / 60.0));
        bench.RunUntil(S(1200 / 60.0 + 0.01));
        Assert.InRange(bench.Served, 1198, 1200);
        Assert.Equal(0, bench.Cadence.Dropped);
    }
}
