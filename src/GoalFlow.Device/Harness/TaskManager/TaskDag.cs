namespace GoalFlow.Device.Harness;

/// <summary>
/// Turns a proposed decomposition into a task DAG that is safe to execute.
///
/// <para>
/// The decomposition comes from an LLM, so it is a SUGGESTION, not a structure.
/// It can name a dependency that doesn't exist, depend on itself, form a cycle,
/// or return forty tasks. None of that may reach the ledger: the Task Manager's
/// legal-transition table assumes a task's dependencies can actually complete,
/// and <c>NextReady</c> on a cycle silently returns nothing — a goal that looks
/// alive and never moves, which is worse than a goal that fails.
/// </para>
///
/// <para>
/// So the model proposes and CODE validates — the same division as the safety
/// gate. Everything here is deterministic and repairs rather than rejects: a
/// bad edge is dropped, not fatal, because a slightly-flatter plan still runs.
/// </para>
/// </summary>
public static class TaskDag
{
    /// <summary>
    /// How many tasks a goal may have. Not arbitrary: each is a planning unit, and
    /// a model asked to decompose freely will happily emit dozens of trivia. Past
    /// this the board stops being glanceable and the token cost stops being worth it.
    /// </summary>
    public const int MaxTasks = 8;

    /// <summary>
    /// Repairs a proposed decomposition into an executable DAG:
    /// caps the count, drops unknown/self edges, and breaks cycles.
    /// Returns the tasks in a runnable order, with what was repaired.
    /// </summary>
    public static (IReadOnlyList<TaskRecord> Tasks, IReadOnlyList<string> Repairs) Sanitize(IReadOnlyList<TaskRecord> proposed)
    {
        var repairs = new List<string>();

        var tasks = proposed.Take(MaxTasks).ToList();
        if (proposed.Count > MaxTasks)
        {
            repairs.Add($"capped {proposed.Count} tasks to {MaxTasks}");
        }

        var known = tasks.Select(t => t.TaskId).ToHashSet(StringComparer.Ordinal);

        // Drop edges that cannot be satisfied: they would leave the task
        // permanently unready and the goal permanently "in progress".
        for (var i = 0; i < tasks.Count; i++)
        {
            var deps = tasks[i].DependsOn
                .Where(d =>
                {
                    if (string.Equals(d, tasks[i].TaskId, StringComparison.Ordinal))
                    {
                        repairs.Add($"{tasks[i].TaskId}: dropped self-dependency");
                        return false;
                    }

                    if (!known.Contains(d))
                    {
                        repairs.Add($"{tasks[i].TaskId}: dropped unknown dependency '{d}'");
                        return false;
                    }

                    return true;
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            tasks[i] = tasks[i] with { DependsOn = deps };
        }

        return (BreakCycles(tasks, repairs), repairs);
    }

    /// <summary>
    /// Topologically sorts, dropping the edges that make cycles.
    ///
    /// <para>
    /// Kahn's algorithm: repeatedly take a task with no unmet dependencies. If
    /// none remains but tasks do, what's left is a cycle — so the earliest of
    /// them is freed by dropping its edges and the sort continues. Dropping an
    /// edge loses ordering; refusing the goal loses the goal.
    /// </para>
    /// </summary>
    private static List<TaskRecord> BreakCycles(List<TaskRecord> tasks, List<string> repairs)
    {
        var remaining = tasks.ToList();
        var ordered = new List<TaskRecord>();
        var done = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(t => t.DependsOn.All(done.Contains));
            if (ready is null)
            {
                // Everything left is in (or behind) a cycle. Free the first one.
                var victim = remaining[0];
                repairs.Add($"{victim.TaskId}: broke a dependency cycle (dropped {string.Join(", ", victim.DependsOn)})");
                ready = victim with { DependsOn = [] };
                remaining[0] = ready;
            }

            remaining.Remove(ready);
            ordered.Add(ready);
            done.Add(ready.TaskId);
        }

        return ordered;
    }
}
