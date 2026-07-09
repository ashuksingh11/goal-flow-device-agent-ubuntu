namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Sustain-phase harness (M4): detects world changes (new/changed calendar
/// events, inventory deltas), applies a MATERIALITY policy, and re-invokes
/// the pipeline loop ONLY when a change is material. Canonical example:
/// "new calendar event overlapping a prep window → material"; a trivial
/// pantry restock → not material.
/// Build effort: FULL logic later. Stub now (M4).
/// </summary>
public interface IChangeWatcher
{
    /// <summary>
    /// Judges one observed change against the active task. Deterministic
    /// policy code (like the safety gate, this is code — not the planner).
    /// </summary>
    MaterialityVerdict Evaluate(WorldChange change, GoalTask task, WorldState world);

    /// <summary>
    /// Watch loop (M4): polls/subscribes to adapters off the virtual clock,
    /// calls <see cref="Evaluate"/>, and invokes <paramref name="onMaterialChange"/>
    /// (the pipeline's adaptation entry point) for material changes.
    /// </summary>
    Task RunAsync(
        Func<WorldChange, CancellationToken, Task> onMaterialChange,
        CancellationToken cancellationToken = default);
}

/// <summary>One observed change in the local world.</summary>
public sealed record WorldChange
{
    /// <summary>Source, e.g. "calendar" | "inventory" | "shopping_list".</summary>
    public required string Source { get; init; }

    /// <summary>"added" | "removed" | "updated".</summary>
    public required string Kind { get; init; }

    /// <summary>Human-readable summary; becomes the proposal's <c>trigger</c> when material.</summary>
    public required string Summary { get; init; }

    /// <summary>Virtual-clock instant observed.</summary>
    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>Materiality decision plus rationale (traced either way).</summary>
public sealed record MaterialityVerdict
{
    public required bool IsMaterial { get; init; }

    /// <summary>Which policy rule fired, e.g. "event_overlaps_prep_window".</summary>
    public required string Rule { get; init; }
}

/// <summary>Skeleton implementation — logic lands in M4.</summary>
public sealed class ChangeWatcher : IChangeWatcher
{
    private readonly IClock _clock;
    private readonly ITrace _trace;

    public ChangeWatcher(IClock clock, ITrace trace)
    {
        _clock = clock;
        _trace = trace;
    }

    public MaterialityVerdict Evaluate(WorldChange change, GoalTask task, WorldState world) =>
        // TODO(M4): policy table — calendar event overlapping a planned prep
        // window => material; cosmetic changes => not material. Trace both.
        throw new NotImplementedException("Design stub (M4).");

    public Task RunAsync(
        Func<WorldChange, CancellationToken, Task> onMaterialChange,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Design stub (M4).");
}
