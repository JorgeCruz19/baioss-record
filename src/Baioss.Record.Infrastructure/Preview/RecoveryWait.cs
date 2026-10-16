using System.Diagnostics;

namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Espera de un reintento de recuperación de la grabación (ver <c>FfmpegChannelEngine.RecoverRecordingAsync</c>) que la VUELTA
/// DE LA SEÑAL puede adelantar. Sin ella el reintento esperaba su backoff entero aunque la fuente ya avisara de que tenía
/// señal: tras caer el proceso de captura de una DeckLink, la tarjeta reabrió en 1,3 s, pero el primer reintento salió 20 ms
/// antes (aún sin señal) y el siguiente 2 s después: 3,3 s de hueco en la grabación en vez de ~1,3 s (medido en una Duo 2).
/// <list type="bullet">
///   <item>Solo se adelanta la espera que sigue a una caída o a un intento SIN señal en la fuente. Si el intento falló CON
///   señal, el fallo es otro (carpeta, codificador…) y se espera el backoff entero aunque la fuente avise: si no, cada aviso
///   relanzaría la reconstrucción en bucle.</item>
///   <item>Cada espera tiene su propio aviso: uno que llegó antes (durante el intento anterior, o en otra caída) no acorta
///   la siguiente. Y como el aviso que llegara entre el intento fallido y la espera se perdería, al empezar se mira la señal:
///   si ya está, no hay nada que esperar.</item>
///   <item>Nunca antes del mínimo que pide el llamador (el nombre por defecto de una pieza lleva la hora con precisión de
///   segundo: la nueva no debe salir en el mismo segundo que la caída).</item>
/// </list>
/// </summary>
internal sealed class RecoveryWait
{
    private TaskCompletionSource? _pending;

    /// <summary>La fuente avisa de que vuelve a tener señal: termina la espera en curso, si es de las que se adelantan.</summary>
    public void SignalReturned() => Volatile.Read(ref _pending)?.TrySetResult();

    /// <summary>
    /// Espera <paramref name="backoff"/> o, con <paramref name="untilSignal"/>, hasta que la fuente tenga señal
    /// (<paramref name="hasSignal"/> al empezar, o un <see cref="SignalReturned"/> durante la espera), lo que llegue antes; y en
    /// cualquier caso al menos <paramref name="minimum"/>.
    /// </summary>
    /// <returns>True si la espera terminó por la señal; false si se cumplió el backoff.</returns>
    public async Task<bool> WaitAsync(TimeSpan backoff, bool untilSignal, Func<bool> hasSignal, TimeSpan minimum)
    {
        var waited = Stopwatch.StartNew();
        bool signal = false;
        if (!untilSignal) await Task.Delay(backoff).ConfigureAwait(false);
        else
        {
            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pending, wake); // la de una espera anterior queda descartada: sus avisos ya no cuentan
            try
            {
                // Publicado el aviso, se mira la señal: si volvió entre el intento fallido y ahora, no llegará ningún aviso.
                signal = hasSignal();
                if (!signal)
                {
                    using var timer = new CancellationTokenSource();
                    signal = await Task.WhenAny(wake.Task, Task.Delay(backoff, timer.Token)).ConfigureAwait(false) == wake.Task;
                    if (signal) timer.Cancel();
                }
            }
            finally { Interlocked.CompareExchange(ref _pending, null, wake); }
        }
        var rest = minimum - waited.Elapsed;
        if (rest > TimeSpan.Zero) await Task.Delay(rest).ConfigureAwait(false);
        return signal;
    }
}
