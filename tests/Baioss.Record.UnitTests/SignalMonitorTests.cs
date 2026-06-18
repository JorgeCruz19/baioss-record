using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Events;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.UnitTests.Fakes;
using Xunit;

namespace Baioss.Record.UnitTests;

public class SignalMonitorTests
{
    private static SignalInfo Locked =>
        new(SignalState.Locked, Resolution.Hd1080, FrameRate.P25, AudioLayout.Stereo, HasAudio: true, null, null);

    private static SignalInfo LockedNoAudio =>
        new(SignalState.Locked, Resolution.Hd1080, FrameRate.P25, AudioLayout.Stereo, HasAudio: false, null, null);

    private static async Task<T> Within<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(2000));
        Assert.True(completed == task, "timeout esperando el evento de dominio");
        return await task;
    }

    [Fact]
    public async Task Lock_Then_Loss_PublishEventsWithChannelId()
    {
        var bus = new InProcessEventBus();
        var channelId = Guid.NewGuid();
        var locked = new TaskCompletionSource<SignalLocked>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource<SignalLost>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s1 = bus.Subscribe<SignalLocked>((e, _) => { locked.TrySetResult(e); return Task.CompletedTask; });
        using var s2 = bus.Subscribe<SignalLost>((e, _) => { lost.TrySetResult(e); return Task.CompletedTask; });

        var source = new FakeCaptureSource();
        await using var monitor = new SignalMonitor(bus, NullLogger<SignalMonitor>.Instance) { ChannelId = channelId };
        await monitor.WatchAsync(source);

        source.Emit(Locked);
        var lockedEvt = await Within(locked.Task);
        Assert.Equal(channelId, lockedEvt.ChannelId);
        Assert.Equal(Resolution.Hd1080, lockedEvt.Resolution);

        source.Emit(SignalInfo.None);
        var lostEvt = await Within(lost.Task);
        Assert.Equal(channelId, lostEvt.ChannelId);
    }

    [Fact]
    public async Task LockedWithoutAudio_PublishesSilence()
    {
        var bus = new InProcessEventBus();
        var silence = new TaskCompletionSource<AudioSilenceDetected>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s = bus.Subscribe<AudioSilenceDetected>((e, _) => { silence.TrySetResult(e); return Task.CompletedTask; });

        var source = new FakeCaptureSource();
        var channelId = Guid.NewGuid();
        await using var monitor = new SignalMonitor(bus, NullLogger<SignalMonitor>.Instance) { ChannelId = channelId };
        await monitor.WatchAsync(source);

        source.Emit(LockedNoAudio);

        var evt = await Within(silence.Task);
        Assert.Equal(channelId, evt.ChannelId);
    }
}
