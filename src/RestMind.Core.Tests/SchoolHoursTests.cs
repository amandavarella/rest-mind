using RestMind.Core;
using Xunit;

namespace RestMind.Core.Tests;

/// <summary>
/// Breaks must never land during the school day: Monday to Friday, 08:30 to 15:10.
/// </summary>
public class SchoolHoursTests
{
    // 2026-01-05 is a Monday, 2026-01-10 a Saturday, 2026-01-11 a Sunday.
    private static DateTimeOffset Weekday(int hour, int minute) =>
        new(2026, 1, 5, hour, minute, 0, TimeSpan.Zero);

    private static (BreakScheduler Scheduler, TestClock Clock) Create(DateTimeOffset start)
    {
        var clock = new TestClock(start);
        return (new BreakScheduler(SchedulerHarness.Config(), null, clock), clock);
    }

    private static EnforcementState StateAt(DateTimeOffset moment)
    {
        var (scheduler, clock) = Create(moment);
        return scheduler.Tick(clock.UtcNow).State;
    }

    [Theory]
    [InlineData(8, 30)]  // first minute of school
    [InlineData(9, 0)]
    [InlineData(12, 0)]
    [InlineData(15, 9)]  // last minute of school
    public void DuringSchool_NothingIsEnforced(int hour, int minute)
    {
        Assert.Equal(EnforcementState.Inactive, StateAt(Weekday(hour, minute)));
    }

    [Theory]
    [InlineData(8, 29)]   // the minute before school starts
    [InlineData(15, 10)]  // the minute school ends
    [InlineData(16, 0)]
    [InlineData(20, 0)]
    public void OutsideSchool_TheCycleRuns(int hour, int minute)
    {
        Assert.Equal(EnforcementState.Working, StateAt(Weekday(hour, minute)));
    }

    [Theory]
    [InlineData(2026, 1, 10)] // Saturday
    [InlineData(2026, 1, 11)] // Sunday
    public void Weekends_HaveNoSchoolDay(int year, int month, int day)
    {
        var middleOfSchoolHours = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(EnforcementState.Working, StateAt(middleOfSchoolHours));
    }

    [Fact]
    public void SchoolCoversTheWholeWorkingWeek()
    {
        // 2026-01-05 is Monday, so the next five days are Monday through Friday.
        for (var offset = 0; offset < 5; offset++)
        {
            var noon = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero).AddDays(offset);

            Assert.Equal(EnforcementState.Inactive, StateAt(noon));
        }
    }

    [Fact]
    public void BreakInProgressWhenSchoolStarts_IsCancelled()
    {
        // A break beginning at 08:25 would otherwise still be covering the screen at 08:35,
        // in the middle of a lesson.
        var (scheduler, clock) = Create(Weekday(8, 25));
        scheduler.StartBreakEarly(clock.UtcNow);
        Assert.Equal(EnforcementState.OnBreak, scheduler.Tick(clock.UtcNow).State);

        clock.UtcNow = Weekday(8, 31);

        Assert.Equal(EnforcementState.Inactive, scheduler.Tick(clock.UtcNow).State);
    }

    [Fact]
    public void SchoolDayDoesNotConsumeTheAfternoonWorkPeriod()
    {
        // Work most of a period before school, sit through the day, and the afternoon should
        // still start from a full 50 minutes rather than dropping straight into a break.
        var (scheduler, clock) = Create(Weekday(8, 0));
        SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(25));

        clock.UtcNow = Weekday(12, 0);
        Assert.Equal(EnforcementState.Inactive, scheduler.Tick(clock.UtcNow).State);

        clock.UtcNow = Weekday(15, 10);
        var afternoon = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.Working, afternoon.State);
        Assert.Equal(TimeSpan.FromMinutes(50), afternoon.Remaining);
    }

    [Fact]
    public void AfterSchool_AFullCycleStillLeadsToABreak()
    {
        var (scheduler, clock) = Create(Weekday(15, 10));

        var status = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(50));

        Assert.Equal(EnforcementState.OnBreak, status.State);
        Assert.Equal(TimeSpan.FromMinutes(10), status.Remaining);
    }

    [Fact]
    public void BeforeSchool_TheCycleCanStartABreak()
    {
        var (scheduler, clock) = Create(Weekday(7, 30));

        // 07:30 plus 50 minutes is 08:20, still ten minutes clear of the school day.
        var status = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(50));

        Assert.Equal(EnforcementState.OnBreak, status.State);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 8, 30, true)]
    [InlineData(DayOfWeek.Friday, 15, 9, true)]
    [InlineData(DayOfWeek.Friday, 15, 10, false)]
    [InlineData(DayOfWeek.Monday, 8, 29, false)]
    [InlineData(DayOfWeek.Saturday, 12, 0, false)]
    [InlineData(DayOfWeek.Sunday, 12, 0, false)]
    public void ContainsMatchesTheSchoolTimetable(DayOfWeek day, int hour, int minute, bool expected)
    {
        Assert.Equal(expected, SchoolHours.Contains(day, new TimeOnly(hour, minute)));
    }
}
