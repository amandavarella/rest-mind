namespace RestMind.Core;

/// <summary>
/// What the enforcer is currently doing. Only <see cref="OnBreak"/> blocks the machine.
/// </summary>
public enum EnforcementState
{
    /// <summary>Outside the configured active hours, or the day is disabled. No enforcement.</summary>
    Inactive,

    /// <summary>Inside a work period. Machine is usable.</summary>
    Working,

    /// <summary>Inside a work period, close enough to the break to warn the user.</summary>
    Warning,

    /// <summary>Break in progress. The machine is blocked.</summary>
    OnBreak,

    /// <summary>A parent has suspended enforcement for a while.</summary>
    PausedByParent,
}
