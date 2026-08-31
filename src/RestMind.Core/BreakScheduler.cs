namespace RestMind.Core;

/// <summary>
/// The authoritative work/break clock. Deterministic and side-effect free: it takes the
/// current time, updates its state, and reports what the enforcer should be doing. All the
/// interesting behaviour lives here so it can be unit tested away from Windows.
/// </summary>
public sealed class BreakScheduler
{
    /// <summary>
    /// The most work-credit a single tick can add. The service ticks about once a second, so
    /// a larger gap means the machine was asleep, hibernating, or off. Clamping here is what
    /// makes work time "time actually spent using the machine" rather than wall-clock.
    /// </summary>
    public const double MaxTickCreditSeconds = 5.0;

    private readonly IClock _clock;

    public BreakScheduler(AppConfig config, SchedulerState? state = null, IClock? clock = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        State = state ?? new SchedulerState();
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Live configuration. Replacing it takes effect on the next tick.</summary>
    public AppConfig Config { get; set; }

    /// <summary>The mutable state the caller is responsible for persisting.</summary>
    public SchedulerState State { get; private set; }

    public SchedulerStatus Tick() => Tick(_clock.UtcNow);

    public SchedulerStatus Tick(DateTimeOffset nowUtc)
    {
        // Only the gap between two consecutive work ticks counts as screen time. Every other
        // path below leaves the flag false, so the tick that ends a break, a pause, or an
        // out-of-hours stretch starts the new work period at zero.
        var elapsed = State.AccruingWork ? CreditableElapsed(nowUtc) : 0;
        State.LastTickUtc = nowUtc;
        State.AccruingWork = false;

        if (State.PausedUntilUtc is { } pausedUntil)
        {
            if (nowUtc < pausedUntil)
            {
                return new SchedulerStatus(EnforcementState.PausedByParent, pausedUntil - nowUtc);
            }

            State.PausedUntilUtc = null;
        }

        var localNow = _clock.ToLocal(nowUtc);
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);

        // School outranks a break in progress, unlike the active-hours window below. A break
        // that started at 08:25 must not still be covering the screen once a lesson begins, so
        // the school day cancels it outright rather than letting it run to completion.
        if (SchoolHours.Contains(localNow.DayOfWeek, localTime))
        {
            BeginWorkPeriod();
            return new SchedulerStatus(EnforcementState.Inactive, TimeSpan.Zero);
        }

        // Outside school, a break that has started always runs to completion, even if the
        // active-hours window closes underneath it. Otherwise working right up to the edge of
        // the window would be a way to skip the break entirely.
        if (State.Phase == CyclePhase.OnBreak)
        {
            if (State.BreakEndsAtUtc is { } breakEnds && nowUtc < breakEnds)
            {
                return new SchedulerStatus(EnforcementState.OnBreak, breakEnds - nowUtc);
            }

            BeginWorkPeriod();
        }

        var day = Config.Schedule.ForDay(localNow.DayOfWeek);
        if (day is null || !day.IsWithinActiveHours(localTime))
        {
            // Entering the active window should always start with a full work period.
            State.WorkAccruedSeconds = 0;
            return new SchedulerStatus(EnforcementState.Inactive, TimeSpan.Zero);
        }

        State.WorkAccruedSeconds += elapsed;

        var workLimitSeconds = Math.Max(1, day.WorkMinutes) * 60.0;
        if (State.WorkAccruedSeconds >= workLimitSeconds)
        {
            return new SchedulerStatus(EnforcementState.OnBreak, BeginBreak(nowUtc, day));
        }

        State.AccruingWork = true;

        var untilBreak = TimeSpan.FromSeconds(workLimitSeconds - State.WorkAccruedSeconds);
        var warning = TimeSpan.FromMinutes(Math.Max(0, Config.PreBreakWarningMinutes));
        var state = warning > TimeSpan.Zero && untilBreak <= warning
            ? EnforcementState.Warning
            : EnforcementState.Working;

        return new SchedulerStatus(state, untilBreak);
    }

    /// <summary>Ends the current break immediately and starts a fresh work period.</summary>
    public void EndBreakEarly() => BeginWorkPeriod();

    /// <summary>Starts a break right now, regardless of how much work has accrued.</summary>
    public void StartBreakEarly(DateTimeOffset nowUtc)
    {
        var day = DayScheduleFor(nowUtc);
        BeginBreak(nowUtc, day);
    }

    /// <summary>
    /// Suspends all enforcement for <paramref name="duration"/>. When the pause expires a full
    /// work period begins, so a pause is never a way to land straight in a break.
    /// </summary>
    public void Pause(DateTimeOffset nowUtc, TimeSpan duration)
    {
        BeginWorkPeriod();
        State.PausedUntilUtc = nowUtc + duration;
    }

    /// <summary>Cancels an active pause and resumes enforcement.</summary>
    public void Resume() => State.PausedUntilUtc = null;

    /// <summary>Replaces the persisted state, e.g. after loading it from disk at startup.</summary>
    public void LoadState(SchedulerState state) => State = state ?? new SchedulerState();

    private TimeSpan BeginBreak(DateTimeOffset nowUtc, DaySchedule? day)
    {
        var minutes = Math.Max(1, day?.BreakMinutes ?? AppConfig.FallbackBreakMinutes);
        var duration = TimeSpan.FromMinutes(minutes);

        State.Phase = CyclePhase.OnBreak;
        State.BreakEndsAtUtc = nowUtc + duration;
        State.WorkAccruedSeconds = 0;
        State.AccruingWork = false;

        return duration;
    }

    private void BeginWorkPeriod()
    {
        State.Phase = CyclePhase.Working;
        State.BreakEndsAtUtc = null;
        State.WorkAccruedSeconds = 0;
        State.AccruingWork = false;
    }

    /// <summary>
    /// Elapsed time since the last tick, clamped so that a backwards clock jump credits
    /// nothing and a long gap (sleep, shutdown) credits almost nothing.
    /// </summary>
    private double CreditableElapsed(DateTimeOffset nowUtc)
    {
        if (State.LastTickUtc is not { } last)
        {
            return 0;
        }

        var seconds = (nowUtc - last).TotalSeconds;
        return seconds <= 0 ? 0 : Math.Min(seconds, MaxTickCreditSeconds);
    }

    private DaySchedule? DayScheduleFor(DateTimeOffset nowUtc) =>
        Config.Schedule.ForDay(_clock.ToLocal(nowUtc).DayOfWeek);
}
