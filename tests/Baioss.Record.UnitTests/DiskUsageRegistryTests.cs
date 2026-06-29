using Baioss.Record.Infrastructure.Storage;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Verifica el registro compartido de ritmo de disco: agrega bytes/s y cuenta canales POR VOLUMEN (para que
/// la guarda de cada canal vea el caudal real con N canales), aísla volúmenes distintos y es robusto ante un
/// canal cuyo ritmo no esté disponible. (Auditoría 24/7, A7/#10.)
/// </summary>
public sealed class DiskUsageRegistryTests
{
    [Fact]
    public void Aggregates_RatesAndCount_PerVolume()
    {
        var reg = new DiskUsageRegistry();
        var c1 = Guid.NewGuid(); var c2 = Guid.NewGuid(); var c3 = Guid.NewGuid();
        reg.Register(c1, @"C:\rec\A", () => 100);
        reg.Register(c2, @"C:\rec\B", () => 200);
        reg.Register(c3, @"D:\rec\C", () => 50); // otro volumen, no debe sumarse al de C:

        Assert.Equal(300, reg.TotalBytesPerSecond(@"C:\rec\otro")); // mismo volumen C: → 100+200
        Assert.Equal(2, reg.ActiveCount(@"C:\cualquiera"));
        Assert.Equal(50, reg.TotalBytesPerSecond(@"D:\loquesea"));  // volumen D: aislado
        Assert.Equal(1, reg.ActiveCount(@"D:\"));
    }

    [Fact]
    public void Unregister_RemovesChannelFromAggregate()
    {
        var reg = new DiskUsageRegistry();
        var c1 = Guid.NewGuid(); var c2 = Guid.NewGuid();
        reg.Register(c1, @"C:\rec", () => 100);
        reg.Register(c2, @"C:\rec", () => 200);

        reg.Unregister(c1);

        Assert.Equal(200, reg.TotalBytesPerSecond(@"C:\rec"));
        Assert.Equal(1, reg.ActiveCount(@"C:\rec"));
    }

    [Fact]
    public void Total_IgnoresThrowingOrNegativeRates()
    {
        var reg = new DiskUsageRegistry();
        reg.Register(Guid.NewGuid(), @"C:\r", () => throw new InvalidOperationException());
        reg.Register(Guid.NewGuid(), @"C:\r", () => -10);
        reg.Register(Guid.NewGuid(), @"C:\r", () => 80);

        Assert.Equal(80, reg.TotalBytesPerSecond(@"C:\r")); // throw → ignorado, negativo → 0
        Assert.Equal(3, reg.ActiveCount(@"C:\r"));
    }
}
