namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Cross-cutting harness: structured record of every decision, tool call,
/// and gate outcome in the pipeline. Doubles as the demo "activity feed"
/// (the cloud/UI render it to show the mechanism working).
/// Build effort: REAL BUT SIMPLE (append-only in-memory list + stdout).
/// </summary>
public interface ITrace
{
    /// <summary>Appends one event (timestamped via IClock by the caller or sink).</summary>
    void Record(TraceEvent evt);

    /// <summary>All events so far, in order.</summary>
    IReadOnlyList<TraceEvent> Events { get; }

    /// <summary>Events for one goal, in order (feeds per-goal activity feed).</summary>
    IReadOnlyList<TraceEvent> For(string goalId);
}

/// <summary>Pipeline phase a trace event belongs to.</summary>
public enum TracePhase
{
    Orchestrate,
    Sense,
    Decide,
    Gate,
    Act,
    Sustain,
    Transport,
}

/// <summary>One structured trace entry.</summary>
public sealed record TraceEvent
{
    /// <summary>Virtual-clock instant.</summary>
    public required DateTimeOffset At { get; init; }

    public string? GoalId { get; init; }

    public required TracePhase Phase { get; init; }

    /// <summary>Emitting harness, e.g. "SafetyGate", "RulesPlanner".</summary>
    public required string Source { get; init; }

    /// <summary>Event kind, e.g. "decision", "tool_call", "gate_outcome", "transition".</summary>
    public required string Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>Optional structured details (serialized with the event).</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}

/// <summary>Skeleton implementation — simple real logic later (in-memory + console sink).</summary>
public sealed class InMemoryTrace : ITrace
{
    private readonly List<TraceEvent> _events = [];

    public IReadOnlyList<TraceEvent> Events => _events;

    public void Record(TraceEvent evt)
    {
        _events.Add(evt);
        Console.Error.WriteLine(
            $"[{evt.At:O}] {evt.Phase}/{evt.Source}/{evt.Kind}: {evt.Message}");
    }

    public IReadOnlyList<TraceEvent> For(string goalId) =>
        _events.Where(evt => string.Equals(evt.GoalId, goalId, StringComparison.Ordinal)).ToArray();
}
