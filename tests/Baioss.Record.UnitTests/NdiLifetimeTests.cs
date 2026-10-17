using System.Diagnostics;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Cierre del runtime NDI al salir: con RUDP, un proceso que recibió NDI y sale sin NDIlib_destroy se queda colgado para siempre
/// (la descarga de la DLL espera a hilos que Windows ya terminó). Se cierra una sola vez, solo si se llegó a cargar, nunca bajo un
/// receptor abierto, y después ya no se crea ninguno.
/// </summary>
public sealed class NdiLifetimeTests
{
    private int _initializes, _destroys;

    private NdiLifetime Lifetime(bool loads = true, bool throws = false) => new(
        () => { _initializes++; if (throws) throw new DllNotFoundException("Processing.NDI.Lib.x64.dll"); return loads; },
        () => _destroys++);

    [Fact]
    public void IfNdiWasNeverUsed_ShutdownDoesNotLoadIt()
    {
        var ndi = Lifetime();
        Assert.Equal(NdiShutdownResult.NotLoaded, ndi.Shutdown(TimeSpan.Zero));
        Assert.Equal(0, _initializes); // cargar la DLL al salir solo para cerrarla sería absurdo
        Assert.Equal(0, _destroys);
    }

    [Fact]
    public void WithNothingOpen_ShutdownDestroysOnce_AndNoMoreReceiversAreCreated()
    {
        var ndi = Lifetime();
        Assert.True(ndi.IsAvailable);

        Assert.Equal(NdiShutdownResult.Destroyed, ndi.Shutdown(TimeSpan.Zero));
        Assert.Equal(NdiShutdownResult.Destroyed, ndi.Shutdown(TimeSpan.Zero)); // la app y el respaldo de ProcessExit
        Assert.Equal(1, _destroys);
        Assert.False(ndi.IsAvailable);
        Assert.False(ndi.TryOpen());
        Assert.Equal(1, _initializes);
    }

    [Fact]
    public void WithAReceiverOpen_ShutdownDoesNotDestroyUnderIt_UntilItIsClosed()
    {
        var ndi = Lifetime();
        Assert.True(ndi.TryOpen());

        Assert.Equal(NdiShutdownResult.InUse, ndi.Shutdown(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(0, _destroys);
        Assert.True(ndi.IsAvailable); // sigue usable: el receptor abierto aún recibe

        ndi.Close();
        Assert.Equal(NdiShutdownResult.Destroyed, ndi.Shutdown(TimeSpan.Zero));
        Assert.Equal(1, _destroys);
    }

    [Fact]
    public async Task ShutdownWaitsForTheReceiversThatAreBeingClosed()
    {
        var ndi = Lifetime();
        Assert.True(ndi.TryOpen());
        Assert.True(ndi.TryOpen());
        var closer = Task.Run(async () =>
        {
            await Task.Delay(150);
            ndi.Close();
            await Task.Delay(150);
            ndi.Close();
        });

        var clock = Stopwatch.StartNew();
        Assert.Equal(NdiShutdownResult.Destroyed, ndi.Shutdown(TimeSpan.FromSeconds(10)));
        Assert.InRange(clock.Elapsed.TotalMilliseconds, 250, 9000); // esperó a los dos, sin agotar el plazo
        Assert.Equal(0, ndi.OpenCount);
        Assert.Equal(1, _destroys);
        await closer;
    }

    [Fact]
    public void Disabled_LeavesTheRuntimeAsBefore()
    {
        var ndi = Lifetime();
        ndi.DestroyOnExit = false;
        Assert.True(ndi.IsAvailable);

        Assert.Equal(NdiShutdownResult.Disabled, ndi.Shutdown(TimeSpan.Zero));
        Assert.Equal(0, _destroys);
        Assert.True(ndi.TryOpen());
    }

    [Theory]
    [InlineData(false, false)] // initialize devuelve false (CPU no compatible…)
    [InlineData(false, true)]  // la DLL no está junto al exe
    public void IfTheRuntimeDoesNotLoad_NothingIsOpenedNorDestroyed(bool loads, bool throws)
    {
        var ndi = Lifetime(loads, throws);
        Assert.False(ndi.IsAvailable);
        Assert.False(ndi.IsAvailable);
        Assert.False(ndi.TryOpen());
        Assert.Equal(NdiShutdownResult.NotLoaded, ndi.Shutdown(TimeSpan.Zero));
        Assert.Equal(1, _initializes); // se intenta una sola vez
        Assert.Equal(0, _destroys);
    }

    [Fact]
    public void OpeningAReceiver_LoadsTheRuntimeOnDemand_AndCountsIt()
    {
        var ndi = Lifetime();
        Assert.True(ndi.TryOpen());
        Assert.True(ndi.TryOpen());
        Assert.Equal(2, ndi.OpenCount);
        ndi.Close();
        ndi.Close();
        ndi.Close(); // un cierre de más no deja la cuenta en negativo
        Assert.Equal(0, ndi.OpenCount);
        Assert.Equal(1, _initializes);
    }
}
