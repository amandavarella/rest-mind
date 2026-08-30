namespace RestMind.Core;

/// <summary>
/// The rules for one day of the week: when enforcement is active and how long the
/// work/break cycle runs.
/// </summary>
public sealed class DaySchedule
{
    public DayOfWeek Day { get; set; }

    /// <summary>When false, the machine is never blocked on this day.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local time enforcement starts.</summary>
    public TimeOnly ActiveStart { get; set; } = new(7, 0);

    /// <summary>
    /// Local time enforcement stops. If this is earlier than <see cref="ActiveStart"/> the
    /// window wraps past midnight (e.g. 20:00 to 02:00).
    /// </summary>
    public TimeOnly ActiveEnd { get; set; } = new(21, 0);

    public int WorkMinutes { get; set; } = 50;

    public int BreakMinutes { get; set; } = 10;

    public bool IsWithinActiveHours(TimeOnly localTime)
    {
        if (!Enabled)
        {
            return false;
        }

        if (ActiveStart == ActiveEnd)
        {
            // A zero-length window means "never", not "always" — safer default.
            return false;
        }

        return ActiveStart < ActiveEnd
            ? localTime >= ActiveStart && localTime < ActiveEnd
            : localTime >= ActiveStart || localTime < ActiveEnd;
    }

    public DaySchedule Clone() => new()
    {
        Day = Day,
        Enabled = Enabled,
        ActiveStart = ActiveStart,
        ActiveEnd = ActiveEnd,
        WorkMinutes = WorkMinutes,
        BreakMinutes = BreakMinutes,
    };
}
