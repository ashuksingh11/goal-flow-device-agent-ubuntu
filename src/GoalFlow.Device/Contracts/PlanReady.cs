using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Device → cloud (<c>type: "plan_ready"</c>): the candidate plan produced by
/// the pipeline, after the safety gate, with all side-effects frozen as
/// proposals. <c>task_status</c> is "awaiting_approval".
/// </summary>
public sealed record PlanReady
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.PlanReady;

    [JsonPropertyName("goal_id")]
    public required string GoalId { get; init; }

    /// <summary>Echoes the dispatch correlation id, e.g. "disp-001".</summary>
    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    /// <summary>See <see cref="TaskStatuses"/>; normally "awaiting_approval".</summary>
    [JsonPropertyName("task_status")]
    public required string TaskStatus { get; init; }

    [JsonPropertyName("payload")]
    public required PlanReadyPayload Payload { get; init; }
}

/// <summary>Payload of <see cref="PlanReady"/>.</summary>
public sealed record PlanReadyPayload
{
    /// <summary>The per-day plan items.</summary>
    [JsonPropertyName("plan")]
    public required IReadOnlyList<PlanItem> Plan { get; init; }

    /// <summary>Frozen side-effects awaiting approval.</summary>
    [JsonPropertyName("proposals")]
    public required IReadOnlyList<ProposalItem> Proposals { get; init; }

    /// <summary>Deterministic safety-gate verdict for this plan.</summary>
    [JsonPropertyName("safety")]
    public required SafetyResult Safety { get; init; }
}

/// <summary>One planned dinner, e.g. { "day":"Mon", "dish":"spinach dal rice bowl", "why":[...] }.</summary>
public sealed record PlanItem
{
    [JsonPropertyName("day")]
    public required string Day { get; init; }

    [JsonPropertyName("dish")]
    public required string Dish { get; init; }

    /// <summary>Machine-readable rationale tags, e.g. ["more_vegetables","uses_inventory"].</summary>
    [JsonPropertyName("why")]
    public IReadOnlyList<string> Why { get; init; } = [];
}

/// <summary>
/// Safety-gate outcome. Produced ONLY by the deterministic
/// <c>ISafetyGate</c> code path — never by the planner.
/// </summary>
public sealed record SafetyResult
{
    /// <summary>"passed" or "blocked".</summary>
    [JsonPropertyName("gate")]
    public required string Gate { get; init; }

    /// <summary>Which hard constraints were violated; empty when passed.</summary>
    [JsonPropertyName("hard_violations")]
    public IReadOnlyList<string> HardViolations { get; init; } = [];

    /// <summary><c>gate</c> value when no hard constraint is violated.</summary>
    public const string GatePassed = "passed";

    /// <summary><c>gate</c> value when the plan is blocked.</summary>
    public const string GateBlocked = "blocked";
}
