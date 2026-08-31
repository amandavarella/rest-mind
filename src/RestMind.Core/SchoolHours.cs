namespace RestMind.Core;

/// <summary>
/// The school day. Breaks never run during these hours, so the machine stays usable for
/// lessons and homework set in class.
/// </summary>
/// <remarks>
/// Deliberately hardcoded for now. When this becomes configurable it should move into
/// <see cref="DaySchedule"/> as a per-day exclusion, and only <see cref="Contains"/> and the
/// config schema need to change; nothing else reaches into these values.
/// </remarks>
public static class SchoolHours
{
    public static readonly TimeOnly Start = new(8, 30);
    public static readonly TimeOnly End = new(15, 10);

    /// <summary>True during a weekday school period. Weekends are never school time.</summary>
    public static bool Contains(DayOfWeek day, TimeOnly localTime) =>
        day is >= DayOfWeek.Monday and <= DayOfWeek.Friday
        && localTime >= Start
        && localTime < End;
}
