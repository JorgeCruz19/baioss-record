namespace Baioss.Record.Application.Licensing;

/// <summary>Datos persistidos del periodo de prueba (los guarda la infraestructura por duplicado).</summary>
/// <param name="StartedAt">Primer arranque conocido.</param>
/// <param name="HighWater">Máximo «ahora» observado nunca: solo sube. Atrasar el reloj no lo baja.</param>
/// <param name="UsedSeconds">Tiempo de uso ACUMULADO medido con reloj monotónico (inmune a cambios de hora).</param>
public sealed record TrialMark(DateTimeOffset StartedAt, DateTimeOffset HighWater, long UsedSeconds);

/// <summary>
/// Cálculo PURO del periodo de prueba. Separado del almacenamiento para poder probar a fondo los casos que de
/// verdad se dan (reloj atrasado, reloj adelantado, primer arranque) sin tocar disco ni registro.
///
/// <para><b>Por qué dos medidas.</b> Contar solo por calendario es manipulable: basta atrasar el reloj. Contar
/// solo el uso acumulado dejaría una prueba eterna a quien abre la app cinco minutos al mes. Se consume lo que
/// marque la MAYOR de las dos: el calendario (con marca de agua, que nunca retrocede) y el uso real acumulado
/// con reloj monotónico. Así ni atrasar la hora ni adelantarla y devolverla congelan la prueba.</para>
/// </summary>
public static class TrialPeriod
{
    /// <summary>Duración por defecto del periodo de prueba.</summary>
    public const int DefaultDays = 14;

    /// <summary>Días restantes y si caducó, a partir de la marca persistida y la hora actual.</summary>
    public static (int DaysRemaining, bool Expired) Evaluate(TrialMark mark, DateTimeOffset now, int trialDays = DefaultDays)
    {
        if (trialDays <= 0) return (0, true);

        // Marca de agua: el «ahora» efectivo nunca es menor que el mayor visto. Atrasar el reloj no da días.
        var effectiveNow = now > mark.HighWater ? now : mark.HighWater;

        // Inicio posterior al «ahora» efectivo (reloj mal puesto al instalar): se toma el momento efectivo, para
        // no regalar días negativos; el desbloqueo real de ese caso lo aporta el uso acumulado, más abajo.
        var start = mark.StartedAt > effectiveNow ? effectiveNow : mark.StartedAt;

        int byCalendar = (int)Math.Floor((effectiveNow - start).TotalDays);
        int byUsage = (int)(mark.UsedSeconds / 86_400);        // uso real, inmune a la hora del sistema
        int consumed = Math.Max(byCalendar, byUsage);

        int remaining = trialDays - consumed;
        if (remaining < 0) remaining = 0;
        return (remaining, remaining == 0);
    }

    /// <summary>
    /// Marca actualizada tras un tick de la app: la marca de agua solo sube y el uso acumulado suma el tiempo
    /// realmente transcurrido (medido fuera, con reloj monotónico). Es lo que se persiste.
    ///
    /// <para><b>Tope de plausibilidad.</b> La marca de agua no puede AVANZAR más deprisa que el tiempo real
    /// medido con el reloj monotónico (con margen ×2 para absorber correcciones legítimas). Sin este tope, un
    /// reloj accidentalmente puesto en el futuro (pila CMOS, año mal tecleado, NTP roto) durante UN solo tick
    /// dejaba HighWater envenenado para siempre y la prueba caducaba entera e irrecuperablemente — en vez de
    /// consumir solo el tiempo que de verdad pasó. El tope NO debilita el anti-retroceso: atrasar el reloj sigue
    /// sin bajar la marca, y el tiempo con la app cerrada nunca lo capturó la marca de agua tampoco antes.</para>
    /// </summary>
    public static TrialMark Advance(TrialMark mark, DateTimeOffset now, TimeSpan elapsedMonotonic)
    {
        var elapsed = elapsedMonotonic > TimeSpan.Zero ? elapsedMonotonic : TimeSpan.Zero;

        var highWater = mark.HighWater;
        if (now > highWater)
        {
            var step = now - highWater;                    // cuánto pretende avanzar el reloj de pared
            var allowed = elapsed + elapsed;               // lo plausible: el doble del tiempo real medido
            highWater += step <= allowed ? step : allowed; // un salto mayor es un reloj roto, no tiempo consumido
        }

        return mark with
        {
            HighWater = highWater,
            UsedSeconds = mark.UsedSeconds + (long)elapsed.TotalSeconds,
        };
    }

    /// <summary>Marca de un primer arranque en <paramref name="now"/>.</summary>
    public static TrialMark Begin(DateTimeOffset now) => new(now, now, 0);
}
