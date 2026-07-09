namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Sustain-phase harness (M4): fires time-based actions (reminder due,
/// proposal expiry sweep, day rollover) off the VIRTUAL clock
/// (<see cref="IClock.WaitUntilAsync"/>) — never wall-clock timers.
/// Build effort: REAL BUT SIMPLE. Named/stubbed now; comes alive in M4.
/// </summary>
public interface IScheduler
{
    /// <summary>Registers an action to fire at a virtual-clock instant.</summary>
    void Schedule(ScheduledAction action);

    /// <summary>Cancels a scheduled action by id; false when unknown.</summary>
    bool Cancel(string actionId);

    /// <summary>Actions not yet fired, soonest first.</summary>
    IReadOnlyList<ScheduledAction> Pending { get; }

    /// <summary>
    /// Runs the firing loop until cancelled: await the next due instant on
    /// the virtual clock, fire, repeat.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>One time-triggered action.</summary>
public sealed record ScheduledAction
{
    public required string Id { get; init; }

    public required string GoalId { get; init; }

    /// <summary>Virtual-clock instant to fire at.</summary>
    public required DateTimeOffset DueAt { get; init; }

    /// <summary>What to do when due (e.g. surface a reminder, expire proposals).</summary>
    public required Func<CancellationToken, Task> Callback { get; init; }

    public string? Description { get; init; }
}

/// <summary>Skeleton implementation — simple real logic in M4.</summary>
public sealed class Scheduler : IScheduler
{
    private readonly IClock _clock;
    private readonly ITrace _trace;

    public Scheduler(IClock clock, ITrace trace)
    {
        _clock = clock;
        _trace = trace;
    }

    public IReadOnlyList<ScheduledAction> Pending =>
        throw new NotImplementedException("Design stub (M4).");

    public void Schedule(ScheduledAction action) =>
        throw new NotImplementedException("Design stub (M4).");

    public bool Cancel(string actionId) =>
        throw new NotImplementedException("Design stub (M4).");

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        // TODO(M4): loop — _clock.WaitUntilAsync(next.DueAt), fire, trace.
        throw new NotImplementedException("Design stub (M4).");
}
