using RestMind.Core;
using Xunit;

namespace RestMind.Core.Tests;

public class BreakSchedulerTests
{
    // A Monday afternoon, after school lets out. These tests exercise the cycle itself; the
    // school-hours exclusion has its own suite in SchoolHoursTests.
    private static readonly DateTimeOffset MondayAfterSchool = new(2026, 1, 5, 16, 0, 0, TimeSpan.Zero);

    private static (BreakScheduler Scheduler, TestClock Clock) Create(
        AppConfig? config = null,
        SchedulerState? state = null,
        DateTimeOffset? start = null)
    {
        var clock = new TestClock(start ?? MondayAfterSchool);
        var scheduler = new BreakScheduler(config ?? SchedulerHarness.Config(), state, clock);
        return (scheduler, clock);
    }

    [Fact]
    public void FirstTick_StartsAFullWorkPeriod()
    {
        var (scheduler, clock) = Create();

        var status = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.Working, status.State);
        Assert.Equal(TimeSpan.FromMinutes(50), status.Remaining);
        Assert.False(status.IsBlocking);
    }

    [Fact]
    public void Working_BecomesBreak_ExactlyAfterTheWorkPeriod()
    {
        var (scheduler, clock) = Create();

        var status = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(50));

        Assert.Equal(EnforcementState.OnBreak, status.State);
        Assert.True(status.IsBlocking);
        Assert.Equal(TimeSpan.FromMinutes(10), status.Remaining);
    }

    [Fact]
    public void Working_WarnsShortlyBeforeTheBreak()
    {
        var (scheduler, clock) = Create(SchedulerHarness.Config(warningMinutes: 2));

        var beforeWindow = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(47));
        Assert.Equal(EnforcementState.Working, beforeWindow.State);

        var inWindow = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(1));
        Assert.Equal(EnforcementState.Warning, inWindow.State);
        Assert.False(inWindow.IsBlocking);
    }

    [Fact]
    public void Warning_IsDisabledWhenWarningMinutesIsZero()
    {
        var (scheduler, clock) = Create(SchedulerHarness.Config(warningMinutes: 0));

        var status = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(49));

        Assert.Equal(EnforcementState.Working, status.State);
    }

    [Fact]
    public void Break_EndsAfterTheBreakPeriod_AndAFreshWorkPeriodBegins()
    {
        var (scheduler, clock) = Create();

        SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(50));
        var status = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(10));

        Assert.Equal(EnforcementState.Working, status.State);
        Assert.Equal(TimeSpan.FromMinutes(50), status.Remaining);
    }

    [Fact]
    public void CustomDurations_AreHonoured()
    {
        var (scheduler, clock) = Create(SchedulerHarness.Config(workMinutes: 25, breakMinutes: 5));

        var status = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(25));

        Assert.Equal(EnforcementState.OnBreak, status.State);
        Assert.Equal(TimeSpan.FromMinutes(5), status.Remaining);
    }

    [Fact]
    public void OutsideActiveHours_NothingIsEnforced()
    {
        var config = SchedulerHarness.Config();
        foreach (var day in config.Schedule.Days)
        {
            day.ActiveStart = new TimeOnly(15, 0);
            day.ActiveEnd = new TimeOnly(20, 0);
        }

        // 07:00 is before both the active window and the school day, so this isolates the
        // active-hours rule from the school-hours rule.
        var (scheduler, clock) = Create(config, start: new DateTimeOffset(2026, 1, 5, 7, 0, 0, TimeSpan.Zero));

        var status = scheduler.Tick(clock.UtcNow);
        Assert.Equal(EnforcementState.Inactive, status.State);
        Assert.Equal(TimeSpan.Zero, status.Remaining);
    }

    [Fact]
    public void DisabledDay_NothingIsEnforced()
    {
        var config = SchedulerHarness.Config();
        config.Schedule.ForDay(DayOfWeek.Monday)!.Enabled = false;

        var (scheduler, clock) = Create(config);

        Assert.Equal(EnforcementState.Inactive, scheduler.Tick(clock.UtcNow).State);
    }

    [Fact]
    public void MissingDayEntry_NothingIsEnforced()
    {
        var config = SchedulerHarness.Config();
        config.Schedule.Days.RemoveAll(d => d.Day == DayOfWeek.Monday);

        var (scheduler, clock) = Create(config);

        Assert.Equal(EnforcementState.Inactive, scheduler.Tick(clock.UtcNow).State);
    }

    [Fact]
    public void OvernightActiveWindow_Wraps_PastMidnight()
    {
        var config = SchedulerHarness.Config();
        foreach (var day in config.Schedule.Days)
        {
            day.ActiveStart = new TimeOnly(20, 0);
            day.ActiveEnd = new TimeOnly(2, 0);
        }

        // Saturday into Sunday, so no school day overlaps and the wrap is what is under test.
        var (evening, eveningClock) = Create(config, start: new DateTimeOffset(2026, 1, 10, 21, 0, 0, TimeSpan.Zero));
        Assert.Equal(EnforcementState.Working, evening.Tick(eveningClock.UtcNow).State);

        var (afterMidnight, midnightClock) = Create(config, start: new DateTimeOffset(2026, 1, 11, 1, 0, 0, TimeSpan.Zero));
        Assert.Equal(EnforcementState.Working, afterMidnight.Tick(midnightClock.UtcNow).State);

        var (daytime, dayClock) = Create(config, start: new DateTimeOffset(2026, 1, 11, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(EnforcementState.Inactive, daytime.Tick(dayClock.UtcNow).State);
    }

    [Fact]
    public void WorkAccrual_ResetsWhileInactive_SoTheWindowAlwaysOpensWithAFullPeriod()
    {
        var config = SchedulerHarness.Config();
        foreach (var day in config.Schedule.Days)
        {
            day.ActiveStart = new TimeOnly(16, 0);
            day.ActiveEnd = new TimeOnly(16, 30);
        }

        var (scheduler, clock) = Create(config);

        // Work 20 of the 50 minutes, then fall out of the active window.
        SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(20));
        clock.UtcNow = new DateTimeOffset(2026, 1, 5, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(EnforcementState.Inactive, scheduler.Tick(clock.UtcNow).State);

        // Next day's window opens with a full work period, not the leftover 30 minutes.
        clock.UtcNow = new DateTimeOffset(2026, 1, 6, 16, 0, 0, TimeSpan.Zero);
        var status = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.Working, status.State);
        Assert.Equal(TimeSpan.FromMinutes(50), status.Remaining);
    }

    [Fact]
    public void MachinePoweredOffDuringWork_DoesNotAccrueWorkTime()
    {
        var (scheduler, clock) = Create();

        SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(10));

        // A three hour gap means the machine was off or asleep; that is not screen time.
        clock.Advance(TimeSpan.FromHours(3));
        var status = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.Working, status.State);
        Assert.InRange(status.Remaining.TotalMinutes, 39.5, 40.0);
    }

    [Fact]
    public void ClockJumpingBackwards_CreditsNoWork()
    {
        var (scheduler, clock) = Create();

        var before = SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(-5));
        var after = scheduler.Tick(clock.UtcNow);

        Assert.Equal(before.Remaining, after.Remaining);
    }

    [Fact]
    public void BreakInProgress_SurvivesAReboot_WithTheRemainingTimeIntact()
    {
        var breakEnds = MondayAfterSchool.AddMinutes(10);
        var persisted = new SchedulerState
        {
            Phase = CyclePhase.OnBreak,
            BreakEndsAtUtc = breakEnds,
            LastTickUtc = null, // cleared by StateStore on load
        };

        var (scheduler, clock) = Create(state: persisted);
        clock.UtcNow = MondayAfterSchool.AddMinutes(4);

        var status = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.OnBreak, status.State);
        Assert.Equal(TimeSpan.FromMinutes(6), status.Remaining);
    }

    [Fact]
    public void BreakThatElapsedWhilePoweredOff_IsOver()
    {
        var persisted = new SchedulerState
        {
            Phase = CyclePhase.OnBreak,
            BreakEndsAtUtc = MondayAfterSchool.AddMinutes(10),
        };

        var (scheduler, clock) = Create(state: persisted);
        clock.UtcNow = MondayAfterSchool.AddMinutes(30);

        var status = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.Working, status.State);
        Assert.Equal(TimeSpan.FromMinutes(50), status.Remaining);
    }

    [Fact]
    public void BreakWithMissingEndTime_IsTreatedAsOver()
    {
        var persisted = new SchedulerState { Phase = CyclePhase.OnBreak, BreakEndsAtUtc = null };

        var (scheduler, clock) = Create(state: persisted);

        Assert.Equal(EnforcementState.Working, scheduler.Tick(clock.UtcNow).State);
    }

    [Fact]
    public void BreakRunsToCompletion_EvenAfterTheActiveWindowCloses()
    {
        var config = SchedulerHarness.Config();
        foreach (var day in config.Schedule.Days)
        {
            day.ActiveStart = new TimeOnly(16, 0);
            day.ActiveEnd = new TimeOnly(17, 0);
        }

        var (scheduler, clock) = Create(config);
        clock.UtcNow = new DateTimeOffset(2026, 1, 5, 16, 55, 0, TimeSpan.Zero);
        scheduler.StartBreakEarly(clock.UtcNow);

        // 17:02 is outside the window, but the break still has three minutes to run.
        clock.UtcNow = new DateTimeOffset(2026, 1, 5, 17, 2, 0, TimeSpan.Zero);
        var status = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.OnBreak, status.State);
        Assert.Equal(TimeSpan.FromMinutes(3), status.Remaining);
    }

    [Fact]
    public void EndBreakEarly_UnblocksAndStartsAFreshWorkPeriod()
    {
        var (scheduler, clock) = Create();
        SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(50));

        scheduler.EndBreakEarly();
        var status = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.Working, status.State);
        Assert.Equal(TimeSpan.FromMinutes(50), status.Remaining);
    }

    [Fact]
    public void StartBreakEarly_BlocksImmediatelyForTheFullBreak()
    {
        var (scheduler, clock) = Create();
        scheduler.Tick(clock.UtcNow);

        scheduler.StartBreakEarly(clock.UtcNow);
        var status = scheduler.Tick(clock.UtcNow);

        Assert.Equal(EnforcementState.OnBreak, status.State);
        Assert.Equal(TimeSpan.FromMinutes(10), status.Remaining);
    }

    [Fact]
    public void Pause_SuspendsEnforcement_ThenResumesWithAFullWorkPeriod()
    {
        var (scheduler, clock) = Create();
        SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(50));
        Assert.Equal(EnforcementState.OnBreak, scheduler.Tick(clock.UtcNow).State);

        scheduler.Pause(clock.UtcNow, TimeSpan.FromMinutes(30));

        var paused = scheduler.Tick(clock.UtcNow);
        Assert.Equal(EnforcementState.PausedByParent, paused.State);
        Assert.False(paused.IsBlocking);
        Assert.Equal(TimeSpan.FromMinutes(30), paused.Remaining);

        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(EnforcementState.PausedByParent, scheduler.Tick(clock.UtcNow).State);

        clock.Advance(TimeSpan.FromMinutes(11));
        var afterPause = scheduler.Tick(clock.UtcNow);
        Assert.Equal(EnforcementState.Working, afterPause.State);
        Assert.Equal(TimeSpan.FromMinutes(50), afterPause.Remaining);
    }

    [Fact]
    public void PauseDoesNotAccrueWorkTime()
    {
        var (scheduler, clock) = Create();
        scheduler.Pause(clock.UtcNow, TimeSpan.FromMinutes(30));

        SchedulerHarness.Run(scheduler, clock, TimeSpan.FromMinutes(30));
        clock.Advance(TimeSpan.FromSeconds(5));
        var status = scheduler.Tick(clock.UtcNow);

        // Only the five seconds since the pause ended count as work; the paused half hour does not.
        Assert.Equal(EnforcementState.Working, status.State);
        Assert.InRange(status.Remaining.TotalMinutes, 49.9, 50.0);
    }

    [Fact]
    public void Resume_CancelsAPauseImmediately()
    {
        var (scheduler, clock) = Create();
        scheduler.Pause(clock.UtcNow, TimeSpan.FromHours(2));
        Assert.Equal(EnforcementState.PausedByParent, scheduler.Tick(clock.UtcNow).State);

        scheduler.Resume();

        Assert.Equal(EnforcementState.Working, scheduler.Tick(clock.UtcNow).State);
    }

    [Fact]
    public void RemainingSeconds_RoundsUp_SoTheCountdownNeverShowsZeroWhileBlocked()
    {
        var status = new SchedulerStatus(EnforcementState.OnBreak, TimeSpan.FromMilliseconds(1500));

        Assert.Equal(2, status.RemainingSeconds);
    }
}
