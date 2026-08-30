using RestMind.Core;

namespace RestMind.Core.Tests;

/// <summary>A clock the tests drive by hand. Local time equals UTC unless an offset is set.</summary>
public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; set; }

    public TimeSpan LocalOffset { get; set; } = TimeSpan.Zero;

    public DateTimeOffset ToLocal(DateTimeOffset utc) => utc.ToOffset(LocalOffset);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public static class SchedulerHarness
{
    /// <summary>
    /// A schedule that is active essentially all day, so tests can focus on the cycle itself.
    /// </summary>
    public static AppConfig Config(int workMinutes = 50, int breakMinutes = 10, int warningMinutes = 2) =>
        new()
        {
            PreBreakWarningMinutes = warningMinutes,
            Schedule = Schedule.CreateDefault(
                workMinutes,
                breakMinutes,
                new TimeOnly(0, 0),
                new TimeOnly(23, 59, 59)),
        };

    /// <summary>
    /// Ticks the scheduler across <paramref name="duration"/> in realistic small steps, so work
    /// accrues the way it does in production, and returns the final status.
    /// </summary>
    public static SchedulerStatus Run(
        BreakScheduler scheduler,
        TestClock clock,
        TimeSpan duration,
        TimeSpan? step = null)
    {
        var stepSize = step ?? TimeSpan.FromSeconds(BreakScheduler.MaxTickCreditSeconds);
        var status = scheduler.Tick(clock.UtcNow);
        var end = clock.UtcNow + duration;

        while (clock.UtcNow < end)
        {
            var next = clock.UtcNow + stepSize;
            clock.UtcNow = next > end ? end : next;
            status = scheduler.Tick(clock.UtcNow);
        }

        return status;
    }
}
