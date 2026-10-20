namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Cuándo pasa el preview del proceso viejo al nuevo en un relevo (Grabar/Detener con una fuente de relé), según qué frame de la
/// fuente enseña cada uno. El proceso nuevo empieza en el frame que pasaba al conectarse al relé, pero tarda 0,1–0,3 s en sacarlo
/// (0,3 con NVENC, que abre la GPU) y luego digiere de golpe lo que esperaba en su socket. Con el mando en su primer frame, el
/// preview saltaba hacia atrás y volvía a pasar por los mismos frames: 4–5 con x264 y 8–10 con NVENC, medido con
/// <c>PreviewHandoverTests</c> el 2026-10-19 («es como que retrocede y vuelve al mismo frame»). Ahora el viejo sigue pintando hasta
/// que el nuevo enseña un frame posterior al último que se vio. Pura y testeable.
/// </summary>
internal static class PreviewTakeover
{
    public enum Verdict
    {
        /// <summary>El nuevo aún va por detrás de lo que ya se vio: su frame se descarta y el viejo sigue pintando.</summary>
        NotYet,
        /// <summary>El nuevo toma el mando con este frame.</summary>
        TakeOver,
        /// <summary>El nuevo toma el mando, pero este frame es el mismo de la fuente que se acaba de ver: no se repite.</summary>
        TakeOverAfterThisFrame,
    }

    /// <param name="position">Frame de la fuente que enseña el frame del proceso nuevo (su número en el flujo del relé; medios
    /// frames con un preview a un frame por campo), o null si no se sabe.</param>
    /// <param name="lastShown">El del último frame que se vio (del proceso viejo), o null si no se sabe.</param>
    /// <param name="oldStillPainting">El proceso viejo sigue entregando frames. Si se paró (se retiró, murió, la fuente se fue), no
    /// hay nada que esperar: mejor imagen que un preview congelado.</param>
    /// <param name="waitedTooLong">El nuevo lleva demasiado sin alcanzar al viejo (va más despacio que el directo): toma el mando
    /// igual, como antes.</param>
    public static Verdict Decide(double? position, double? lastShown, bool oldStillPainting, bool waitedTooLong)
    {
        if (position is not { } p || lastShown is not { } last || !oldStillPainting || waitedTooLong) return Verdict.TakeOver;
        if (p > last) return Verdict.TakeOver;
        return p == last ? Verdict.TakeOverAfterThisFrame : Verdict.NotYet;
    }
}
