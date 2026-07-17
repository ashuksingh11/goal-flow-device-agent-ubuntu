namespace GoalFlow.Device.Harness;

/// <summary>
/// Where one task is in its life.
///
/// <para>
/// v2 had ten <c>TaskStatuses</c> STRING CONSTANTS and no machine — a goal's
/// state was whatever the last frame happened to say, and "what is this goal
/// doing right now" had no answer you could ask code for. That is why Agent
/// Board's progress %, ETA and next-step could only ever have been heuristics
/// (plan-day vs clock) rather than facts.
/// </para>
///
/// <para>
/// The legal transitions are CODE, not config: they are an invariant of what a
/// task is, not a product's opinion. Task CONTENT is data.
/// </para>
/// </summary>
public enum TaskState
{
    /// <summary>Created, dependencies not yet satisfied.</summary>
    Created,

    /// <summary>Dependencies satisfied — may be planned now.</summary>
    Ready,

    /// <summary>The planner is working on it.</summary>
    Planning,

    /// <summary>Planned; waiting on a human decision.</summary>
    AwaitingApproval,

    /// <summary>Approved effects are being actuated.</summary>
    Executing,

    /// <summary>Done for now; the observers are watching the world for it.</summary>
    Monitoring,

    /// <summary>A material change arrived; a scoped re-plan is in flight.</summary>
    Adapting,

    /// <summary>Blocked on something outside the agent (a precheck; M3).</summary>
    Paused,

    /// <summary>Failed but retryable — see <see cref="TaskRecord.RetryCount"/>.</summary>
    Retrying,

    /// <summary>Finished successfully. Terminal.</summary>
    Completed,

    /// <summary>Gave up — see <see cref="TaskRecord.FailureReason"/>. Terminal.</summary>
    Failed,

    /// <summary>Abandoned (control: reset, or the goal was dropped). Terminal.</summary>
    Cancelled
}

/// <summary>
/// One unit of work inside a goal: what it is, what it waits on, and where it is.
/// The device planner produces these by decomposing the goal — the cloud never
/// sends them (it has no grounded way to know them).
/// </summary>
public sealed record TaskRecord
{
    public required string TaskId { get; init; }

    public required string GoalId { get; init; }

    /// <summary>Human-readable — this is what Agent Board shows as "next step".</summary>
    public required string Title { get; init; }

    /// <summary>Task ids that must complete first. The edges of the DAG.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Capability modules this task is expected to touch (advisory, from the planner).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public TaskState State { get; internal set; } = TaskState.Created;

    /// <summary>How many times this task has been retried (M3's precheck failures drive this).</summary>
    public int RetryCount { get; internal set; }

    /// <summary>Why it failed, when it did.</summary>
    public string? FailureReason { get; internal set; }

    /// <summary>True once it can never change again.</summary>
    public bool IsTerminal => State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled;
}
