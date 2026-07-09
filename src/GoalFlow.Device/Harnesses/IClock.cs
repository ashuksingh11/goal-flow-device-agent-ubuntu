namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Virtual clock abstraction — cross-cutting.
/// <para>
/// DISCIPLINE: device code NEVER calls <c>DateTime.Now</c>/<c>UtcNow</c> or
/// wall-clock timers directly. Everything reads this interface, so demos can
/// time-travel ("it is now Tuesday evening") and the Tizen port swaps in a
/// platform clock adapter without touching harness code.
/// </para>
/// </summary>
public interface IClock
{
    /// <summary>Current virtual instant.</summary>
    DateTimeOffset Now { get; }

    /// <summary>Current virtual local date (derived from <see cref="Now"/>).</summary>
    DateOnly Today { get; }

    /// <summary>
    /// Completes when the virtual clock reaches <paramref name="until"/>.
    /// The Scheduler awaits this instead of <c>Task.Delay</c>.
    /// </summary>
    Task WaitUntilAsync(DateTimeOffset until, CancellationToken cancellationToken = default);
}

/// <summary>
/// Demo/test clock: starts at a fixed anchor (seed data uses 2026-07-12) and
/// only moves when told to. Skeleton — implementation later.
/// </summary>
public sealed class VirtualClock : IClock
{
    private DateTimeOffset _now;

    /// <param name="start">Initial virtual instant, e.g. 2026-07-12T09:00+00:00 for the seed world.</param>
    public VirtualClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset Now => _now;

    public DateOnly Today => DateOnly.FromDateTime(_now.Date);

    /// <summary>Advance the virtual clock (drives Scheduler/ChangeWatcher in demos).</summary>
    public void Advance(TimeSpan delta) => _now += delta;

    public Task WaitUntilAsync(DateTimeOffset until, CancellationToken cancellationToken = default) =>
        _now >= until ? Task.CompletedTask : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}
