namespace RestMind.Core;

/// <summary>The phase the cycle is in. Persisted, so it survives a reboot.</summary>
public enum CyclePhase
{
    Working,
    OnBreak,
}

/// <summary>
/// The scheduler's persisted state. Written to disk on every phase transition so that a
/// reboot in the middle of a break resumes that break rather than clearing it.
/// </summary>
/// <remarks>
/// Work and break time are measured differently on purpose:
/// <list type="bullet">
/// <item>Break time is wall-clock (<see cref="BreakEndsAtUtc"/>). Shutting the machine down
/// mid-break still counts as resting, and rebooting cannot shorten a break.</item>
/// <item>Work time accrues only while the service is actually running
/// (<see cref="WorkAccruedSeconds"/>). A machine that sat powered off for three hours should
/// not demand a break the moment it boots.</item>
/// </list>
/// </remarks>
public sealed class SchedulerState
{
    public CyclePhase Phase { get; set; } = CyclePhase.Working;

    /// <summary>Wall-clock end of the current break. Only meaningful when <see cref="Phase"/> is OnBreak.</summary>
    public DateTimeOffset? BreakEndsAtUtc { get; set; }

    /// <summary>Seconds of active use accrued in the current work period.</summary>
    public double WorkAccruedSeconds { get; set; }

    /// <summary>Enforcement is suspended by a parent until this time.</summary>
    public DateTimeOffset? PausedUntilUtc { get; set; }

    /// <summary>Last time <see cref="BreakScheduler.Tick"/> ran, used to measure real elapsed use.</summary>
    public DateTimeOffset? LastTickUtc { get; set; }

    /// <summary>
    /// True when the previous tick ended inside a work period. Only then does the gap since that
    /// tick count as work, which keeps break time, paused time, and out-of-hours time from
    /// silently eating into the next work period.
    /// </summary>
    public bool AccruingWork { get; set; }

    public SchedulerState Clone() => new()
    {
        Phase = Phase,
        BreakEndsAtUtc = BreakEndsAtUtc,
        WorkAccruedSeconds = WorkAccruedSeconds,
        PausedUntilUtc = PausedUntilUtc,
        LastTickUtc = LastTickUtc,
        AccruingWork = AccruingWork,
    };
}
