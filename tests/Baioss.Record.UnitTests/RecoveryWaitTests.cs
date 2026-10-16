using System.Diagnostics;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// La espera entre reintentos de recuperación de la grabación: la vuelta de la señal la adelanta (DeckLink en una Duo 2: la
/// tarjeta reabría en 1,3 s y el reintento salía 2 s después de otro fallido → 3,3 s de hueco), pero un aviso viejo no acorta
/// la siguiente, un fallo CON señal no se reintenta en bucle con cada aviso, y nunca sale antes del mínimo.
/// </summary>
public class RecoveryWaitTests
{
    [Fact]
    public async Task TheSignalReturningDuringTheBackoff_EndsTheWaitThen()
    {
        // El caso medido: el primer reintento salió sin señal; la tarjeta reabre a mitad del backoff siguiente (2 s).
        var wait = new RecoveryWait();
        var sw = Stopwatch.StartNew();
        var waiting = wait.WaitAsync(TimeSpan.FromSeconds(2), untilSignal: true, hasSignal: () => false, minimum: TimeSpan.Zero);
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        wait.SignalReturned();

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(1)), "la espera debía terminar por la señal");
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.25, 1.0); // al volver la señal, no a los 2 s del backoff
    }

    [Fact]
    public async Task AfterAFailureWithSignal_TheBackoffIsKept_HoweverOftenTheSourceAnnouncesIt()
    {
        // El intento falló CON señal (carpeta, codificador…): adelantarlo con cada aviso de la fuente sería un bucle sin pausa.
        var wait = new RecoveryWait();
        var sw = Stopwatch.StartNew();
        var waiting = wait.WaitAsync(TimeSpan.FromMilliseconds(600), untilSignal: false, hasSignal: () => true, minimum: TimeSpan.Zero);
        while (!waiting.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            wait.SignalReturned();
            await Task.Delay(20);
        }

        Assert.False(await waiting, "no debía terminar por la señal");
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(550), $"terminó a los {sw.ElapsedMilliseconds} ms: no respetó el backoff");
    }

    [Fact]
    public async Task AnAnnouncementFromBeforeTheWait_DoesNotCutItShort()
    {
        // Un aviso que llegó sin espera en curso (durante el intento anterior, en otra caída) ya no vale.
        var wait = new RecoveryWait();
        wait.SignalReturned();
        wait.SignalReturned();

        var sw = Stopwatch.StartNew();
        Assert.False(await wait.WaitAsync(TimeSpan.FromMilliseconds(400), untilSignal: true, hasSignal: () => false, minimum: TimeSpan.Zero));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(350), $"terminó a los {sw.ElapsedMilliseconds} ms por un aviso viejo");
    }

    [Fact]
    public async Task WhenTheSourceAlreadyHasSignal_ItDoesNotWaitForAnAnnouncementThatWillNotCome()
    {
        // La señal volvió entre el intento fallido y la espera: su aviso ya pasó y no habrá otro.
        var wait = new RecoveryWait();
        var sw = Stopwatch.StartNew();

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5), untilSignal: true, hasSignal: () => true, minimum: TimeSpan.Zero));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"esperó {sw.ElapsedMilliseconds} ms con la señal ya presente");
    }

    [Fact]
    public async Task TheMinimumIsKept_EvenIfTheSignalIsBackAtOnce()
    {
        // La pieza nueva no puede salir en el mismo segundo que la caída: su nombre por defecto lleva la hora al segundo.
        var wait = new RecoveryWait();
        var sw = Stopwatch.StartNew();

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5), untilSignal: true, hasSignal: () => true, minimum: TimeSpan.FromMilliseconds(400)));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(380), $"salió a los {sw.ElapsedMilliseconds} ms, antes del mínimo");
    }

    [Fact]
    public async Task OnlyTheCurrentWaitIsWoken_NotOneLeftOverFromAnEarlierRecording()
    {
        // Una cadena de recuperación de una grabación ya detenida puede seguir esperando su backoff cuando cae la siguiente: el
        // aviso es para la espera más reciente (la vieja verá que ya no es su grabación al terminar la suya).
        var wait = new RecoveryWait();
        var older = wait.WaitAsync(TimeSpan.FromMilliseconds(800), untilSignal: true, hasSignal: () => false, minimum: TimeSpan.Zero);
        var current = wait.WaitAsync(TimeSpan.FromSeconds(5), untilSignal: true, hasSignal: () => false, minimum: TimeSpan.Zero);
        await Task.Delay(100);

        wait.SignalReturned();

        Assert.True(await current.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(await older.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
