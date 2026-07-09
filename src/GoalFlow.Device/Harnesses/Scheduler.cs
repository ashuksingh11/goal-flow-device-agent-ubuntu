namespace GoalFlow.Device.Harnesses;

using GoalFlow.Device.Contracts;

/// <summary>
/// Sustain-phase harness (M4): fires time-based actions (reminder due,
/// proposal expiry sweep, day rollover) off the VIRTUAL clock
/// (<see cref="IClock.WaitUntilAsync"/>) — never wall-clock timers.
/// Build effort: REAL BUT SIMPLE. Named/stubbed now; comes alive in M4.
/// </summary>
public interface IScheduler
{
    /// <summary>Advances the virtual clock one day and runs the sustain tick for the new date.</summary>
    Task<SustainTickResult> AdvanceDayAsync(
        string goalId,
        Func<DateOnly, CancellationToken, Task<SustainTickResult>> sustainTick,
        CancellationToken cancellationToken = default);

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

public sealed record SustainTickResult
{
    public required StatusMessage Status { get; init; }

    public Proposal? Proposal { get; init; }
}

/// <summary>Simple virtual-clock scheduler implementation.</summary>
public sealed class Scheduler : IScheduler
{
    private readonly IClock _clock;
    private readonly ITrace _trace;

    public Scheduler(IClock clock, ITrace trace)
    {
        _clock = clock;
        _trace = trace;
    }

    private readonly List<ScheduledAction> _pending = [];

    public IReadOnlyList<ScheduledAction> Pending =>
        _pending.OrderBy(action => action.DueAt).ToArray();

    public async Task<SustainTickResult> AdvanceDayAsync(
        string goalId,
        Func<DateOnly, CancellationToken, Task<SustainTickResult>> sustainTick,
        CancellationToken cancellationToken = default)
    {
        if (_clock is not VirtualClock virtualClock)
        {
            throw new InvalidOperationException("advance_day requires a VirtualClock.");
        }

        virtualClock.Advance(TimeSpan.FromDays(1));
        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = goalId,
            Phase = TracePhase.Sustain,
            Source = nameof(Scheduler),
            Kind = "advance_day",
            Message = $"virtual clock advanced to {_clock.Today:yyyy-MM-dd}",
        });

        await FireDueAsync(cancellationToken);
        return await sustainTick(_clock.Today, cancellationToken);
    }

    public void Schedule(ScheduledAction action)
    {
        Cancel(action.Id);
        _pending.Add(action);
        _pending.Sort((left, right) => left.DueAt.CompareTo(right.DueAt));
    }

    public bool Cancel(string actionId)
    {
        var removed = _pending.RemoveAll(action => string.Equals(action.Id, actionId, StringComparison.Ordinal));
        return removed > 0;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = Pending.FirstOrDefault();
            if (next is null)
            {
                await _clock.WaitUntilAsync(DateTimeOffset.MaxValue, cancellationToken);
                continue;
            }

            await _clock.WaitUntilAsync(next.DueAt, cancellationToken);
            await FireDueAsync(cancellationToken);
        }
    }

    private async Task FireDueAsync(CancellationToken cancellationToken)
    {
        foreach (var action in _pending.Where(action => action.DueAt <= _clock.Now).ToArray())
        {
            _pending.Remove(action);
            _trace.Record(new TraceEvent
            {
                At = _clock.Now,
                GoalId = action.GoalId,
                Phase = TracePhase.Sustain,
                Source = nameof(Scheduler),
                Kind = "scheduled_action",
                Message = action.Description ?? action.Id,
            });
            await action.Callback(cancellationToken);
        }
    }
}
