using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Scheduling;
using Xunit;

namespace Baioss.Record.UnitTests;

public class ScheduleValidatorTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(-6);
    private static DateTimeOffset At(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, Off);

    private static ScheduledJob Job(Guid ch, RecurrenceKind kind, DateTimeOffset runAt, int durMin, Weekdays days = Weekdays.None) => new()
    {
        ChannelId = ch, Action = ScheduledAction.StartRecording, RunAt = runAt,
        Recurrence = kind, Weekdays = days, Duration = TimeSpan.FromMinutes(durMin),
    };

    // --- RecurrenceInterval ---

    [Fact]
    public void RecurrenceInterval_Daily_Is24h()
        => Assert.Equal(TimeSpan.FromDays(1), ScheduleValidator.RecurrenceInterval(Job(Guid.NewGuid(), RecurrenceKind.Daily, At(2026, 6, 20, 20, 0), 60)));

    [Fact]
    public void RecurrenceInterval_Once_IsNull()
        => Assert.Null(ScheduleValidator.RecurrenceInterval(Job(Guid.NewGuid(), RecurrenceKind.Once, At(2026, 6, 20, 20, 0), 60)));

    [Theory]
    [InlineData(Weekdays.Monday | Weekdays.Tuesday, 1)]                          // consecutivos → 1 día
    [InlineData(Weekdays.Monday | Weekdays.Wednesday | Weekdays.Friday, 2)]      // L-X-V → 2 días
    [InlineData(Weekdays.Monday, 7)]                                            // un solo día → 7 días
    public void RecurrenceInterval_Weekly_IsMinGapBetweenDays(Weekdays days, int expectedDays)
        => Assert.Equal(TimeSpan.FromDays(expectedDays),
            ScheduleValidator.RecurrenceInterval(Job(Guid.NewGuid(), RecurrenceKind.Weekly, At(2026, 6, 20, 20, 0), 60, days)));

    // --- DurationFitsInterval (1) ---

    [Theory]
    [InlineData(RecurrenceKind.Daily, Weekdays.None, 23 * 60, true)]            // 23 h < 24 h
    [InlineData(RecurrenceKind.Daily, Weekdays.None, 24 * 60, false)]           // 24 h alcanza la siguiente
    [InlineData(RecurrenceKind.Daily, Weekdays.None, 25 * 60, false)]
    [InlineData(RecurrenceKind.Weekly, Weekdays.Monday | Weekdays.Tuesday, 23 * 60, true)]
    [InlineData(RecurrenceKind.Weekly, Weekdays.Monday | Weekdays.Tuesday, 25 * 60, false)]
    [InlineData(RecurrenceKind.Once, Weekdays.None, 100 * 60, true)]            // única: no se auto-solapa
    public void DurationFitsInterval(RecurrenceKind kind, Weekdays days, int durMin, bool expected)
        => Assert.Equal(expected, ScheduleValidator.DurationFitsInterval(Job(Guid.NewGuid(), kind, At(2026, 6, 22, 20, 0), durMin, days)));

    // --- SpansToNextDay (5) ---

    [Fact]
    public void SpansToNextDay_True_WhenCrossesMidnight()
        => Assert.True(ScheduleValidator.SpansToNextDay(Job(Guid.NewGuid(), RecurrenceKind.Daily, At(2026, 6, 20, 23, 0), 180))); // 23:00 + 3 h

    [Fact]
    public void SpansToNextDay_False_WithinSameDay()
        => Assert.False(ScheduleValidator.SpansToNextDay(Job(Guid.NewGuid(), RecurrenceKind.Daily, At(2026, 6, 20, 10, 0), 120)));

    // --- Overlaps (2) ---

    [Fact]
    public void Overlaps_True_WhenSameChannelDailyWindowsIntersect()
    {
        var ch = Guid.NewGuid();
        var a = Job(ch, RecurrenceKind.Daily, At(2026, 6, 20, 20, 0), 60);   // 20:00-21:00
        var b = Job(ch, RecurrenceKind.Daily, At(2026, 6, 20, 20, 30), 60);  // 20:30-21:30
        Assert.True(ScheduleValidator.Overlaps(a, b, At(2026, 6, 20, 0, 0)));
    }

    [Fact]
    public void Overlaps_False_WhenWindowsDoNotIntersect()
    {
        var ch = Guid.NewGuid();
        var a = Job(ch, RecurrenceKind.Daily, At(2026, 6, 20, 20, 0), 60);   // 20:00-21:00
        var b = Job(ch, RecurrenceKind.Daily, At(2026, 6, 20, 22, 0), 60);   // 22:00-23:00
        Assert.False(ScheduleValidator.Overlaps(a, b, At(2026, 6, 20, 0, 0)));
    }

    [Fact]
    public void Overlaps_False_OnDifferentChannels()
    {
        var a = Job(Guid.NewGuid(), RecurrenceKind.Daily, At(2026, 6, 20, 20, 0), 60);
        var b = Job(Guid.NewGuid(), RecurrenceKind.Daily, At(2026, 6, 20, 20, 30), 60);
        Assert.False(ScheduleValidator.Overlaps(a, b, At(2026, 6, 20, 0, 0)));
    }

    [Fact]
    public void Overlaps_True_DailyVsOnceOnSameDay()
    {
        var ch = Guid.NewGuid();
        var daily = Job(ch, RecurrenceKind.Daily, At(2026, 6, 20, 20, 0), 60);
        var once = Job(ch, RecurrenceKind.Once, At(2026, 6, 25, 20, 30), 60);
        Assert.True(ScheduleValidator.Overlaps(daily, once, At(2026, 6, 20, 0, 0)));
    }

    // --- NextRecurringAnchor (3) ---

    [Fact]
    public void NextRecurringAnchor_Daily_RollsToTomorrow_WhenTimePassed()
    {
        var now = At(2026, 6, 20, 21, 0);  // las 20:00 de hoy ya pasaron
        var anchor = ScheduleValidator.NextRecurringAnchor(RecurrenceKind.Daily, Weekdays.None, TimeSpan.FromHours(20), Off, now);
        Assert.Equal(At(2026, 6, 21, 20, 0), anchor);
    }

    [Fact]
    public void NextRecurringAnchor_Daily_StaysToday_WhenTimeAhead()
    {
        var now = At(2026, 6, 20, 19, 0);
        var anchor = ScheduleValidator.NextRecurringAnchor(RecurrenceKind.Daily, Weekdays.None, TimeSpan.FromHours(20), Off, now);
        Assert.Equal(At(2026, 6, 20, 20, 0), anchor);
    }

    [Fact]
    public void NextRecurringAnchor_Weekly_PicksNextSelectedDay()
    {
        var now = At(2026, 6, 22, 10, 0);
        var targetDay = now.AddDays(1).DayOfWeek;  // mañana
        var anchor = ScheduleValidator.NextRecurringAnchor(RecurrenceKind.Weekly, ScheduleEvaluator.ToFlag(targetDay), TimeSpan.FromHours(20), Off, now);
        Assert.Equal(At(2026, 6, 23, 20, 0), anchor);
        Assert.Equal(targetDay, anchor.DayOfWeek);
    }
}
