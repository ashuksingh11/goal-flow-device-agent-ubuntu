namespace GoalFlow.Device.Modules.Steering;

/// <summary>
/// HARNESS MODULE: Scheduler / Temporal (clock half).
/// The GENERIC clock every other module reads. INVARIANT: nothing in the agent
/// ever hardcodes a date — "today" is either the real system date or a
/// simulated date driven by <c>control</c> frames (set_date / advance_day).
/// Mock-world dates are stored as day OFFSETS and resolved against this clock.
/// </summary>
public interface IClock
{
    /// <summary>Current instant (UTC).</summary>
    DateTimeOffset Now { get; }

    /// <summary>Current date — the anchor all relative offsets resolve against.</summary>
    DateOnly Today { get; }
}

/// <summary>Real wall clock — the default when no <c>--date</c>/set_date is in play.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}

/// <summary>
/// Demo/simulation clock. Starts at real today (or a supplied date) and is
/// driven by <c>control</c> frames: <c>set_date</c> → <see cref="SetDate"/>,
/// <c>advance_day</c> → <see cref="AdvanceDay"/>.
/// </summary>
public sealed class SimulatedClock : IClock
{
    private DateOnly _today;

    /// <summary>Starts at <paramref name="start"/>, or real today when null.</summary>
    public SimulatedClock(DateOnly? start = null)
        => _today = start ?? DateOnly.FromDateTime(DateTime.UtcNow);

    public DateTimeOffset Now => new(_today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);

    public DateOnly Today => _today;

    /// <summary>Handles <c>control: set_date</c>. <paramref name="isoDate"/> e.g. "2026-07-14".</summary>
    public void SetDate(string isoDate)
    {
        // TODO(M1): parse ISO date, validate, emit a status frame with the new sim_date.
        throw new NotImplementedException("v2-M0 design skeleton");
    }

    /// <summary>Handles <c>control: advance_day</c>; returns the new today.</summary>
    public DateOnly AdvanceDay()
    {
        // TODO(M1): step _today forward one day and let MonitorAdapt re-evaluate the world.
        throw new NotImplementedException("v2-M0 design skeleton");
    }
}
