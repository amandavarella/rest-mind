namespace RestMind.Core;

/// <summary>Indirection over the system clock so the scheduler can be tested deterministically.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>The local time corresponding to <paramref name="utc"/>, used for active-hours checks.</summary>
    DateTimeOffset ToLocal(DateTimeOffset utc);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset ToLocal(DateTimeOffset utc) => utc.ToLocalTime();
}
