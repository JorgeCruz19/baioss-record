using Baioss.Record.Application.Channels;
using Xunit;

namespace Baioss.Record.UnitTests;

public class DropAlarmTrackerTests
{
    [Fact]
    public void NoDrops_NeverAlarms()
    {
        var t = new DropAlarmTracker(onAfter: 3, offAfter: 5);
        for (long i = 0; i < 10; i++) Assert.False(t.Update(0)); // total acumulado estable
        Assert.False(t.Active);
    }

    [Fact]
    public void SustainedDrops_RaiseAfterThreshold()
    {
        var t = new DropAlarmTracker(onAfter: 3, offAfter: 5);
        Assert.False(t.Update(0)); // línea base
        Assert.False(t.Update(2)); // +drops (1)
        Assert.False(t.Update(5)); // +drops (2)
        Assert.True(t.Update(9));  // +drops (3) → alarma
    }

    [Fact]
    public void IsolatedDrop_DoesNotAlarm()
    {
        var t = new DropAlarmTracker(onAfter: 3, offAfter: 5);
        t.Update(0);               // base
        t.Update(1);               // un único incremento
        Assert.False(t.Update(1)); // se estabiliza → nunca alcanza la racha
        Assert.False(t.Active);
    }

    [Fact]
    public void DropsThenStable_ClearsAfterOffThreshold()
    {
        var t = new DropAlarmTracker(onAfter: 2, offAfter: 3);
        t.Update(0); t.Update(1); t.Update(2); // racha de 2 → activa
        Assert.True(t.Active);

        Assert.True(t.Update(2));  // estable (1 sin drops)
        Assert.True(t.Update(2));  // estable (2)
        Assert.False(t.Update(2)); // estable (3) → se apaga
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var t = new DropAlarmTracker(onAfter: 2, offAfter: 5);
        t.Update(0); t.Update(1); t.Update(2);
        Assert.True(t.Active);
        t.Reset();
        Assert.False(t.Active);
        Assert.False(t.Update(100)); // tras Reset, la primera muestra es de nuevo línea base
    }
}
