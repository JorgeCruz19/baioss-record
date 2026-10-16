using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>El resumen del preview para el registro: cadencias, cuadros irregulares según la razón pantalla/fuente y los
/// contadores del planificador como diferencia entre intervalos.</summary>
public class PreviewPresentationStatsTests
{
    [Theory]
    [InlineData(new[] { 2, 2, 2, 2 }, 2.002, 0)]       // 29.97 en 60 Hz
    [InlineData(new[] { 2, 1, 3, 2 }, 2.002, 50)]
    [InlineData(new[] { 2, 3, 2, 3, 2 }, 2.4, 0)]      // 25 en 60 Hz: dos o tres
    [InlineData(new[] { 2, 3, 4, 1 }, 2.4, 50)]
    [InlineData(new[] { 1, 1, 2, 1 }, 1.001, 25)]      // 59.94 en 60 Hz
    [InlineData(new[] { 1, 1, 1, 2 }, 0.5, 25)]        // fuente más rápida que la pantalla
    public void IrregularFrames_DependOnTheRefreshesEachFrameShouldGet(int[] holds, double refreshesPerFrame, double expected)
        => Assert.Equal(expected, PreviewPresentationStats.IrregularPercent(holds, refreshesPerFrame), 3);

    [Fact]
    public void AnInterval_ReportsTheCadences_AndTheSchedulerCountersAsDifferences()
    {
        var stats = new PreviewPresentationStats();
        stats.Close(0, late: 5, skipped: 1); // contadores acumulados de antes del intervalo
        double t = 0;
        for (int tick = 0; tick < 600; tick++) // 10 s a 60 Hz, un frame nuevo cada dos refrescos
        {
            t = tick / 60.0;
            if (tick % 2 == 0) stats.OnArrival(t, 0.030);
            stats.OnTick(t, shown: tick % 2 == 1);
        }
        var r = stats.Close(10, late: 7, skipped: 1);
        Assert.NotNull(r);
        Assert.InRange(r!.Value.SourceFps, 29.9, 30.1);
        Assert.InRange(r.Value.DisplayHz, 59.9, 60.1);
        Assert.Equal(0, r.Value.IrregularPercent);
        Assert.Equal(30, r.Value.MedianLeadMs, 6);
        Assert.Equal(2, r.Value.Late);
        Assert.Equal(0, r.Value.Skipped);
        Assert.Null(stats.Close(11, 7, 1)); // sin llegadas, nada que decir
    }
}
