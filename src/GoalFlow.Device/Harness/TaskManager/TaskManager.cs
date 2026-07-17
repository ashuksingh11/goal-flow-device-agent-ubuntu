using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;

namespace GoalFlow.Device.Harness;

/// <summary>
/// HARNESS COMPONENT 4: Task Manager — the goal ledger.
///
/// <para>
/// THE POINT: Agent Board shows "Birthday Party, 68%, next step: buy party
/// decorations, ETA 2 days, 3 pending, 2 alerts". None of that can be honest
/// unless something actually knows what the goal is made of. v2 had ten status
/// STRINGS and a Dictionary of loose context — so progress could only ever have
/// been guessed from plan-day vs the clock. This makes it derivable instead.
/// </para>
///
/// <para>
/// Replaces GoalAgent's private _activeGoals dictionary. Concurrent by
/// construction: Program dispatches every frame on its own Task.Run, so two
/// goals genuinely do mutate this at once.
/// </para>
/// </summary>
public sealed class TaskManager
{
    private readonly ConcurrentDictionary<string, GoalRecord> _goals = new(StringComparer.Ordinal);
    private readonly ILogger<TaskManager> _logger;
    private readonly Func<TaskRecord, Task>? _onTransition;

    public TaskManager(ILogger<TaskManager> logger, Func<TaskRecord, Task>? onTransition = null)
    {
        _logger = logger;
        _onTransition = onTransition;
    }

    /// <summary>
    /// The legal moves. Code, not config — this is what a task IS, not a product's
    /// opinion about it. An illegal move is a bug in the caller, so it is logged
    /// and refused rather than silently applied: a lifecycle you can't trust is
    /// worse than no lifecycle, because Agent Board would report its fiction
    /// confidently.
    /// </summary>
    private static readonly Dictionary<TaskState, TaskState[]> Legal = new()
    {
        [TaskState.Created] = [TaskState.Ready, TaskState.Cancelled],
        [TaskState.Ready] = [TaskState.Planning, TaskState.Paused, TaskState.Cancelled],
        [TaskState.Planning] = [TaskState.AwaitingApproval, TaskState.Executing, TaskState.Monitoring, TaskState.Failed, TaskState.Retrying, TaskState.Cancelled],
        [TaskState.AwaitingApproval] = [TaskState.Executing, TaskState.Monitoring, TaskState.Cancelled, TaskState.Failed],
        [TaskState.Executing] = [TaskState.Monitoring, TaskState.Completed, TaskState.Failed, TaskState.Retrying, TaskState.Cancelled],
        [TaskState.Monitoring] = [TaskState.Adapting, TaskState.Completed, TaskState.Cancelled],
        [TaskState.Adapting] = [TaskState.AwaitingApproval, TaskState.Monitoring, TaskState.Cancelled],
        [TaskState.Paused] = [TaskState.Ready, TaskState.Cancelled, TaskState.Failed],
        [TaskState.Retrying] = [TaskState.Ready, TaskState.Failed, TaskState.Cancelled],
        [TaskState.Completed] = [],
        [TaskState.Failed] = [],
        [TaskState.Cancelled] = []
    };

    /// <summary>Registers a goal and its tasks. Tasks come from the DEVICE planner's decomposition.</summary>
    public GoalRecord CreateGoal(Dispatch dispatch, IReadOnlyList<TaskRecord> tasks, JsonObject worldSnapshot)
    {
        var record = new GoalRecord
        {
            Dispatch = dispatch,
            Tasks = tasks.ToList(),
            WorldSnapshot = worldSnapshot
        };

        _goals[dispatch.GoalId] = record;
        _logger.LogInformation("goal_created {GoalId} tasks={TaskCount}", dispatch.GoalId, tasks.Count);
        return record;
    }

    public GoalRecord? GetGoal(string goalId) => _goals.TryGetValue(goalId, out var g) ? g : null;

    public void RemoveGoal(string goalId) => _goals.TryRemove(goalId, out _);

    public IReadOnlyList<GoalRecord> ActiveGoals => _goals.Values.ToArray();

    /// <summary>
    /// The next task whose dependencies are all satisfied — the DAG's ready
    /// frontier. This is what Agent Board renders as "next step", and what a
    /// per-task planner would pull from.
    /// </summary>
    public TaskRecord? NextReady(string goalId)
    {
        var goal = GetGoal(goalId);
        if (goal is null)
        {
            return null;
        }

        var done = goal.Tasks.Where(t => t.State == TaskState.Completed).Select(t => t.TaskId).ToHashSet(StringComparer.Ordinal);
        return goal.Tasks.FirstOrDefault(t => !t.IsTerminal && t.DependsOn.All(done.Contains));
    }

    /// <summary>
    /// Moves a task, if the move is legal. Returns false (and logs) otherwise —
    /// callers get an answer instead of a corrupted ledger.
    /// </summary>
    public async Task<bool> TransitionAsync(string goalId, string taskId, TaskState next, string? reason = null)
    {
        var task = GetGoal(goalId)?.Tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null)
        {
            _logger.LogWarning("task_transition_unknown {GoalId}/{TaskId} -> {Next}", goalId, taskId, next);
            return false;
        }

        if (task.State == next)
        {
            return true;
        }

        if (!Legal[task.State].Contains(next))
        {
            _logger.LogWarning("task_transition_illegal {GoalId}/{TaskId} {From} -> {To} (refused)", goalId, taskId, task.State, next);
            return false;
        }

        var from = task.State;
        task.State = next;
        if (next == TaskState.Retrying)
        {
            task.RetryCount++;
        }

        if (reason is not null && next is TaskState.Failed or TaskState.Paused)
        {
            task.FailureReason = reason;
        }

        _logger.LogInformation("task_transition {GoalId}/{TaskId} {From} -> {To} {Reason}", goalId, taskId, from, next, reason ?? "");
        if (_onTransition is not null)
        {
            await _onTransition(task);
        }

        return true;
    }

    /// <summary>Attaches the plan a task produced, and its world snapshot.</summary>
    public void AttachPlan(string goalId, IReadOnlyList<PlanItem> plan, JsonObject? snapshot = null)
    {
        var goal = GetGoal(goalId);
        if (goal is null)
        {
            return;
        }

        goal.Plan = plan;
        if (snapshot is not null)
        {
            goal.WorldSnapshot = snapshot;
        }
    }
}

/// <summary>
/// One goal the device is sustaining: the contract, its task DAG, the live plan,
/// and the world as it was when planned.
///
/// <para>Absorbs v2's ActiveGoalContext, which had no notion of tasks at all.</para>
/// </summary>
public sealed class GoalRecord
{
    public required Dispatch Dispatch { get; init; }

    public required List<TaskRecord> Tasks { get; init; }

    /// <summary>The live plan — SETTABLE so an approved adaptation can patch it in
    /// place without recreating the record (which would reset the dedup set).</summary>
    public IReadOnlyList<PlanItem> Plan { get; set; } = [];

    public JsonObject WorldSnapshot { get; set; } = new();

    /// <summary>Change keys already surfaced — the "exactly once" half of the materiality gate.</summary>
    public HashSet<string> EmittedMaterialChanges { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How far along, 0-100 — DERIVED from task state, not guessed from the clock.
    /// This is the number Agent Board shows, and the reason the Task Manager exists.
    /// </summary>
    public int ProgressPercent => Tasks.Count == 0
        ? 0
        : (int)Math.Round(100.0 * Tasks.Count(t => t.State == TaskState.Completed) / Tasks.Count);

    /// <summary>Tasks not yet finished — Agent Board's "pending".</summary>
    public int PendingTasks => Tasks.Count(t => !t.IsTerminal);

    /// <summary>True once every task reached a terminal state.</summary>
    public bool IsComplete => Tasks.Count > 0 && Tasks.All(t => t.IsTerminal);
}
