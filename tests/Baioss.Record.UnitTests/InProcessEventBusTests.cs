using Baioss.Record.Domain.Events;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Infrastructure.Messaging;
using Xunit;

namespace Baioss.Record.UnitTests;

public class InProcessEventBusTests
{
    [Fact]
    public async Task Publish_DeliversToMatchingSubscriber()
    {
        var bus = new InProcessEventBus();
        RecordingStarted? received = null;
        using var _ = bus.Subscribe<RecordingStarted>((e, _) => { received = e; return Task.CompletedTask; });

        var evt = new RecordingStarted(Guid.NewGuid(), Guid.NewGuid(), "op");
        await bus.PublishAsync(evt);

        Assert.Same(evt, received);
    }

    [Fact]
    public async Task Subscribe_ByBaseInterface_ReceivesAllEvents()
    {
        var bus = new InProcessEventBus();
        int count = 0;
        using var _ = bus.Subscribe<IDomainEvent>((_, _) => { count++; return Task.CompletedTask; });

        await bus.PublishAsync(new RecordingStarted(Guid.NewGuid(), Guid.NewGuid(), null));
        await bus.PublishAsync(new SignalLost(Guid.NewGuid()));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Publish_DoesNotDeliverToNonMatchingSubscriber()
    {
        var bus = new InProcessEventBus();
        int count = 0;
        using var _ = bus.Subscribe<RecordingStarted>((_, _) => { count++; return Task.CompletedTask; });

        await bus.PublishAsync(new SignalLost(Guid.NewGuid()));

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Dispose_Subscription_StopsDelivery()
    {
        var bus = new InProcessEventBus();
        int count = 0;
        var sub = bus.Subscribe<SignalLocked>((_, _) => { count++; return Task.CompletedTask; });

        await bus.PublishAsync(new SignalLocked(Guid.NewGuid(), Resolution.Hd1080, FrameRate.P25));
        sub.Dispose();
        await bus.PublishAsync(new SignalLocked(Guid.NewGuid(), Resolution.Hd1080, FrameRate.P25));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Publish_IsolatesSubscriberExceptions()
    {
        var bus = new InProcessEventBus();
        bool secondRan = false;
        using var s1 = bus.Subscribe<SignalLost>((_, _) => throw new InvalidOperationException("boom"));
        using var s2 = bus.Subscribe<SignalLost>((_, _) => { secondRan = true; return Task.CompletedTask; });

        await bus.PublishAsync(new SignalLost(Guid.NewGuid()));   // no debe propagar la excepción

        Assert.True(secondRan);
    }
}
