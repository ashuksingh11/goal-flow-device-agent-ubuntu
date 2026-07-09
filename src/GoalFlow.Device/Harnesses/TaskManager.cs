using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Orchestrate-phase harness: creates the device-side task for an incoming
/// Task Contract, owns the goal_id ↔ task mapping, and drives the status
/// lifecycle (created → planning → awaiting_approval → executing → adapting → done).
/// Build effort: FULL logic later.
/// </summary>
public interface ITaskManager
{
    /// <summary>
    /// Creates (or re-attaches on redelivery — dedupe on correlation_id) the
    /// device task for a dispatched contract. Initial status: "created".
    /// </summary>
    GoalTask CreateTask(Dispatch contract);

    /// <summary>
    /// Transitions the task to <paramref name="newStatus"/> (a
    /// <see cref="TaskStatuses"/> value). Throws on an illegal transition.
    /// </summary>
    void Transition(string goalId, string newStatus);

    /// <summary>Current lifecycle status for the goal.</summary>
    string GetStatus(string goalId);

    /// <summary>Looks up a live task; null when unknown.</summary>
    GoalTask? Find(string goalId);
}

/// <summary>Device-side task record for one goal contract.</summary>
public sealed record GoalTask
{
    public required string GoalId { get; init; }

    /// <summary>The contract as dispatched (immutable input to the pipeline).</summary>
    public required Dispatch Contract { get; init; }

    /// <summary>Current <see cref="TaskStatuses"/> value.</summary>
    public required string Status { get; init; }

    /// <summary>Virtual-clock instant the task was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Latest approved/planned meal slice used by sustain materiality checks.</summary>
    public IReadOnlyList<PlanItem> ActivePlan { get; init; } = [];
}

/// <summary>Skeleton implementation — full logic in the implementation phase.</summary>
public sealed class TaskManager : ITaskManager
{
    private readonly IClock _clock;
    private readonly ITrace _trace;

    public TaskManager(IClock clock, ITrace trace)
    {
        _clock = clock;
        _trace = trace;
    }

    public GoalTask CreateTask(Dispatch contract) =>
        // TODO: dedupe on contract.CorrelationId; record trace event; status = created.
        throw new NotImplementedException("Design stub.");

    public void Transition(string goalId, string newStatus) =>
        // TODO: validate against the legal lifecycle graph; trace every transition.
        throw new NotImplementedException("Design stub.");

    public string GetStatus(string goalId) =>
        throw new NotImplementedException("Design stub.");

    public GoalTask? Find(string goalId) =>
        throw new NotImplementedException("Design stub.");
}
