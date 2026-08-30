namespace RestMind.Core;

/// <summary>
/// The full weekly schedule. Stored as a list rather than a dictionary so the JSON config
/// stays readable and hand-editable.
/// </summary>
public sealed class Schedule
{
    public List<DaySchedule> Days { get; set; } = new();

    /// <summary>
    /// The rules for <paramref name="day"/>, or null when no entry exists (treated as "no
    /// enforcement").
    /// </summary>
    public DaySchedule? ForDay(DayOfWeek day) => Days.FirstOrDefault(d => d.Day == day);

    /// <summary>A sensible starting point: 50/10 cycles, 07:00-21:00, every day.</summary>
    public static Schedule CreateDefault(
        int workMinutes = 50,
        int breakMinutes = 10,
        TimeOnly? activeStart = null,
        TimeOnly? activeEnd = null)
    {
        var start = activeStart ?? new TimeOnly(7, 0);
        var end = activeEnd ?? new TimeOnly(21, 0);

        return new Schedule
        {
            Days = Enum.GetValues<DayOfWeek>()
                .Select(day => new DaySchedule
                {
                    Day = day,
                    Enabled = true,
                    ActiveStart = start,
                    ActiveEnd = end,
                    WorkMinutes = workMinutes,
                    BreakMinutes = breakMinutes,
                })
                .ToList(),
        };
    }
}
