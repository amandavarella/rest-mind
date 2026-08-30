namespace RestMind.Core;

/// <summary>
/// The result of a scheduler tick: everything the UI needs to render, and nothing it can
/// change. The service computes this; the agent only displays it.
/// </summary>
/// <param name="State">What the enforcer is doing right now.</param>
/// <param name="Remaining">
/// Time left in the current state: until the break ends when blocked, until the break starts
/// when working, until enforcement resumes when paused. <see cref="TimeSpan.Zero"/> when
/// inactive.
/// </param>
public sealed record SchedulerStatus(EnforcementState State, TimeSpan Remaining)
{
    /// <summary>True when the machine should be covered by the overlay.</summary>
    public bool IsBlocking => State == EnforcementState.OnBreak;

    public int RemainingSeconds => (int)Math.Ceiling(Remaining.TotalSeconds);
}
