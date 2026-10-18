namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Cadencia del vídeo que el receptor NDI sirve a FFmpeg: a la tasa DECLARADA, un periodo tras otro, aunque la fuente entregue MENOS
/// cuadros de los que declara (2026-10-17: «SRT (vMix - Output 1)» declara 29.97 y entrega ~10 —sus propias marcas saltan 2–7
/// periodos—; una salida de grafismo declara 60 y manda ~4, cuando cambia la imagen; cuando FFmpeg marcaba el vídeo por contador, en
/// 21 s de grabación quedaban 4,9 s y 1,4 s de vídeo). Aquí se decide qué periodo ocupa cada cuadro real y cuántas veces se repite
/// el último para que lo servido vaya a la cadencia declarada. El receptor solo le envía a FFmpeg los cuadros reales, cada uno con la
/// marca de su periodo (NUT): las repeticiones son periodos que corren y que la grabación rellena repitiendo el anterior.
/// <list type="bullet">
///   <item><b>Cuántos periodos ocupa cada cuadro real:</b> los que pasaron desde el anterior según el RELOJ DE LA FUENTE (la marca
///   <c>timestamp</c> que el SDK pone al enviarlo), no según la llegada: el vaivén de la red (vMix llega con hasta 150 ms) no se
///   toma por un hueco ni la deriva entre los relojes de los dos equipos se acumula. Se redondea arrastrando el resto, para una
///   fuente que manda fuera de rejilla (el grafismo, cada 12,1–12,8 periodos); el resto se fuga un poco en cada cuadro, para que
///   unas ppm de deriva entre el reloj de la fuente y su cadencia real (un conversor SDI→NDI) no sumen nunca un cuadro. Sin marca,
///   o si el reloj de la fuente salta respecto a la llegada (el primer cuadro de una conexión puede ser de hace segundos; un ajuste
///   de hora en el emisor), cuenta la llegada.</item>
///   <item><b>Cuándo se repite:</b> con nuestro reloj, en cuanto el siguiente cuadro va <see cref="Grace"/> periodos tarde, para que
///   los periodos (y el silencio de una fuente sin audio) sigan al reloj. Al llegar el cuadro real se cuadra la cuenta: las
///   repeticiones que falten corren ya, delante de él; si se repitió de más (el cuadro solo venía tarde), se sirve igualmente con
///   ese adelanto, que absorbe el siguiente hueco de la fuente. El adelanto se acota: nunca más de <see cref="MaxAhead"/> cuadros,
///   ni más de <see cref="AheadHold"/> seguido (una fuente a su ritmo no tiene huecos que lo absorban): entonces se descarta un
///   cuadro real, que pasa igualmente a ser el que se repite.</item>
///   <item><b>Sin pasarse:</b> una fuente que va a su ritmo no recibe ninguna repetición (cada cuadro llega antes de vencer el
///   plazo), y un cuadro que cae en el mismo periodo que el anterior, según la fuente, no ocupa otro (una que manda más cuadros de
///   los que declara; la ráfaga con la que OBS por TCP vacía su cola al conectar): pasa a ser el que se repite, sin servirse.</item>
/// </list>
/// Cada periodo —con su cuadro real o repetido— lleva su silencio cuando la fuente no trae audio (<see cref="NdiAudioGapFiller"/>),
/// así que el audio sigue a las marcas del vídeo. Puro: el reloj (monótono) lo pone el llamador, siempre desde el mismo hilo (o
/// bajo el mismo candado).
/// </summary>
internal sealed class NdiFrameCadence
{
    private bool _started;
    private int _rateN, _rateD;
    private double _periodTicks;     // periodo declarado, en ticks de 100 ns (la unidad de las marcas NDI)
    private TimeSpan _lastArrival;   // llegada del último cuadro real (nuestro reloj)
    private TimeSpan? _lastSource;   // su marca en el reloj de la fuente, si la trae
    private double _remainder;       // resto del redondeo de periodos, en periodos
    private int _ahead;              // cuadros servidos de más respecto a la fuente tras el último cuadro real
    private TimeSpan _aheadSince;    // desde cuándo se va por delante (o desde el último descarte para corregirlo)
    private int _repeatsSinceFrame;  // repeticiones servidas desde el último cuadro real

    /// <summary>Lo que hay que hacer con un cuadro real que acaba de llegar: servir antes <see cref="RepeatsBefore"/> veces el
    /// cuadro anterior (los periodos que la fuente se saltó y aún no se rellenaron) y luego servirlo a él, o no (<see cref="Serve"/>
    /// false: lo servido iba por delante de la fuente; el cuadro pasa a ser igualmente el que se repite).</summary>
    public readonly record struct Decision(int RepeatsBefore, bool Serve);

    /// <summary>Periodos de retraso del siguiente cuadro que se esperan antes de repetir el último. Con uno, una fuente a su ritmo
    /// tendría que llegar dos periodos después del anterior para provocar una repetición (OBS: 1,14 como mucho).</summary>
    public double Grace { get; init; } = 1.0;

    /// <summary>Cuadros que lo servido puede ir por delante de la fuente (repeticiones de un cuadro que solo venía tarde) antes de
    /// descartar cuadros reales.</summary>
    public int MaxAhead { get; init; } = 2;

    /// <summary>Tiempo que se tolera ir por delante: pasado, se descarta un cuadro real (uno por vez, otra vez tras el mismo
    /// tiempo) hasta cuadrar. Así el audio no queda adelantado al vídeo indefinidamente en una fuente que no tiene huecos.</summary>
    public TimeSpan AheadHold { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Diferencia entre lo que avanzan el reloj de la fuente y la llegada entre dos cuadros a partir de la cual el de la
    /// fuente no se cree (saltó) y cuenta la llegada.</summary>
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Fracción del resto que se olvida en cada cuadro real: absorbe una deriva de hasta la mitad (500 ppm) sin que
    /// llegue a sumar un cuadro, y es inapreciable sobre el resto de una fuente fuera de rejilla (gira entre −0,5 y 0,5).</summary>
    public double RemainderLeak { get; init; } = 0.001;

    /// <summary>Repeticiones servidas y cuadros reales descartados desde el principio.</summary>
    public long Repeated { get; private set; }
    public long Dropped { get; private set; }

    /// <summary>
    /// Llegó un cuadro real. <paramref name="sourceTime"/> es su marca en el reloj de la fuente (null si no la trae) y
    /// <paramref name="rateN"/>/<paramref name="rateD"/> la tasa declarada con la que el proceso del canal lee el vídeo. El primer
    /// cuadro (o el primero tras <see cref="Reset"/> o un cambio de tasa) se sirve tal cual y fija la referencia.
    /// </summary>
    public Decision OnFrame(TimeSpan now, TimeSpan? sourceTime, int rateN, int rateD)
    {
        if (rateN <= 0 || rateD <= 0) { Reset(); return new Decision(0, true); } // sin tasa no hay cadencia que mantener
        if (!_started || rateN != _rateN || rateD != _rateD)
        {
            Start(now, sourceTime, rateN, rateD);
            return new Decision(0, true);
        }

        // Periodos desde el cuadro anterior: los del reloj de la fuente si los dos relojes cuentan lo mismo (salvo el vaivén de la
        // red); si no, los de la llegada, y el resto arrastrado ya no vale.
        TimeSpan sinceArrival = now - _lastArrival;
        double periods = sinceArrival.Ticks / _periodTicks;
        if (sourceTime is { } source && _lastSource is { } previous)
        {
            TimeSpan sinceSource = source - previous;
            if ((sinceSource - sinceArrival).Duration() <= MaxClockSkew) periods = sinceSource.Ticks / _periodTicks;
            else _remainder = 0;
        }
        double exact = periods + _remainder;
        int slots = Math.Max(0, (int)Math.Floor(exact + 0.5));
        _remainder = (exact - slots) * (1 - RemainderLeak);

        _lastArrival = now;
        _lastSource = sourceTime;

        // Servido hasta ahora: el último cuadro real (en su periodo), el adelanto que llevaba y las repeticiones de después. Este
        // cuadro va «slots» periodos detrás de aquel: faltan (o sobran, si es negativo) tantas repeticiones antes de servirlo.
        int missing = slots - 1 - _ahead - _repeatsSinceFrame;
        _repeatsSinceFrame = 0;
        if (missing >= 0)
        {
            Repeated += missing;
            _ahead = 0;
            return new Decision(missing, true);
        }
        // Sobran repeticiones (o el cuadro cae en el mismo periodo que el anterior): servirlo dejaría lo servido «ahead» cuadros
        // por delante de la fuente.
        int ahead = -missing;
        bool wasAhead = _ahead > 0;
        if (!wasAhead) _aheadSince = now;
        bool serve;
        if (slots == 0) serve = false;                 // mismo periodo que el anterior: no ocupa otro
        else if (ahead > MaxAhead) serve = false;      // demasiado adelanto
        else if (wasAhead && now - _aheadSince >= AheadHold)
        {
            serve = false;                             // el adelanto no se absorbió: se corrige un cuadro
            _aheadSince = now;
        }
        else serve = true;                             // se sirve por delante: lo absorberá el siguiente hueco de la fuente
        if (serve)
        {
            _ahead = ahead;
            return new Decision(0, true);
        }
        Dropped++;
        _ahead = ahead - 1; // no se sirve: su periodo ya lo ocupa una repetición (o el cuadro anterior)
        return new Decision(0, false);
    }

    /// <summary>Repeticiones del último cuadro que vencen ya: el siguiente lleva <see cref="Grace"/> periodos de retraso. Se
    /// cuentan como servidas.</summary>
    public int Due(TimeSpan now)
    {
        if (!_started) return 0;
        double elapsed = (now - _lastArrival).Ticks / _periodTicks;
        int due = (int)Math.Floor(elapsed - Grace) - _ahead - _repeatsSinceFrame;
        if (due <= 0) return 0;
        _repeatsSinceFrame += due;
        Repeated += due;
        return due;
    }

    /// <summary>Cuánto falta para que venza la siguiente repetición (para no dormir de más esperando cuadros); null si no hay
    /// ningún cuadro que repetir.</summary>
    public TimeSpan? UntilNextRepeat(TimeSpan now)
    {
        if (!_started) return null;
        double periods = 1 + _ahead + _repeatsSinceFrame + Grace;
        var due = _lastArrival + TimeSpan.FromTicks((long)Math.Ceiling(periods * _periodTicks));
        return due > now ? due - now : TimeSpan.Zero;
    }

    /// <summary>Olvida la referencia (la fuente dejó de enviar, cambió de formato): no se repite nada hasta el siguiente cuadro
    /// real, que vuelve a empezar.</summary>
    public void Reset()
    {
        _started = false;
        _lastSource = null;
        _remainder = 0;
        _ahead = 0;
        _repeatsSinceFrame = 0;
    }

    private void Start(TimeSpan now, TimeSpan? sourceTime, int rateN, int rateD)
    {
        Reset();
        _started = true;
        _rateN = rateN;
        _rateD = rateD;
        _periodTicks = (double)TimeSpan.TicksPerSecond * rateD / rateN;
        _lastArrival = now;
        _lastSource = sourceTime;
    }
}
