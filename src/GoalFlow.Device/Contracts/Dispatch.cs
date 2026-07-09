using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// The Task Contract, cloud → device (<c>type: "dispatch"</c>).
/// The cloud agent decomposes the user goal into this contract and relays it.
/// <para>
/// INVARIANT: <see cref="Constraints"/>.<see cref="TaskConstraints.Hard"/> is
/// the ONLY thing the safety gate reads. Soft constraints are planner hints.
/// </para>
/// </summary>
public sealed record Dispatch
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Dispatch;

    /// <summary>Goal identity; carried on every task message. E.g. "meal-2026-w29".</summary>
    [JsonPropertyName("goal_id")]
    public required string GoalId { get; init; }

    /// <summary>
    /// Correlation/dedupe key for this dispatch (e.g. "disp-001"). The device
    /// echoes it on the resulting plan_ready. Nullable because the frozen v0
    /// example omits it; when absent the device treats the goal_id as the key.
    /// </summary>
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }

    /// <summary>Natural-language objective, e.g. "healthier family dinners, less food waste".</summary>
    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("scope")]
    public required TaskScope Scope { get; init; }

    [JsonPropertyName("time_window")]
    public required TimeWindow TimeWindow { get; init; }

    [JsonPropertyName("constraints")]
    public required TaskConstraints Constraints { get; init; }

    /// <summary>Optimization targets, e.g. ["reduce_processed","reduce_waste"].</summary>
    [JsonPropertyName("optimization")]
    public IReadOnlyList<string> Optimization { get; init; } = [];

    /// <summary>Autonomy mode; v0 uses <see cref="AutonomyModes.ProposeAll"/>.</summary>
    [JsonPropertyName("autonomy")]
    public string Autonomy { get; init; } = AutonomyModes.ProposeAll;

    [JsonPropertyName("context_hints")]
    public ContextHints? ContextHints { get; init; }

    /// <summary>Cloud-side knowledge-base address for replies, e.g. "kb/device/meal-2026-w29".</summary>
    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; init; }
}

/// <summary>Which slice of family life the contract covers.</summary>
public sealed record TaskScope
{
    /// <summary>Meal slot, e.g. "dinner".</summary>
    [JsonPropertyName("meal")]
    public required string Meal { get; init; }

    /// <summary>Days covered, e.g. ["Mon","Tue","Wed","Thu","Fri"].</summary>
    [JsonPropertyName("days")]
    public required IReadOnlyList<string> Days { get; init; }
}

/// <summary>Inclusive date window (ISO-8601 dates), e.g. 2026-07-13 → 2026-07-17.</summary>
public sealed record TimeWindow
{
    [JsonPropertyName("start")]
    public required string Start { get; init; }

    [JsonPropertyName("end")]
    public required string End { get; init; }
}

/// <summary>Hard + soft constraint blocks.</summary>
public sealed record TaskConstraints
{
    /// <summary>Deterministic blockers — the safety gate's ONLY input.</summary>
    [JsonPropertyName("hard")]
    public required HardConstraints Hard { get; init; }

    /// <summary>Preferences — planner hints, never gate input.</summary>
    [JsonPropertyName("soft")]
    public SoftConstraints? Soft { get; init; }
}

/// <summary>
/// Hard constraints. Violating any of these BLOCKS the plan at the safety
/// gate ("LLM plans, code checks").
/// </summary>
public sealed record HardConstraints
{
    /// <summary>Allergens that must never appear, e.g. ["peanut"].</summary>
    [JsonPropertyName("allergens")]
    public IReadOnlyList<string> Allergens { get; init; } = [];

    /// <summary>Dietary rules, e.g. ["no_pork"].</summary>
    [JsonPropertyName("dietary")]
    public IReadOnlyList<string> Dietary { get; init; } = [];

    /// <summary>Medical restrictions, e.g. ["low_sodium"].</summary>
    [JsonPropertyName("medical")]
    public IReadOnlyList<string> Medical { get; init; } = [];
}

/// <summary>Soft constraints — steer the planner, never block.</summary>
public sealed record SoftConstraints
{
    /// <summary>Ingredients/dishes to avoid when possible, e.g. ["mushrooms"].</summary>
    [JsonPropertyName("dislikes")]
    public IReadOnlyList<string> Dislikes { get; init; } = [];

    /// <summary>Directional preferences, e.g. ["more_vegetables","more_protein"].</summary>
    [JsonPropertyName("prefer")]
    public IReadOnlyList<string> Prefer { get; init; } = [];
}

/// <summary>Free-form hints the cloud relays from conversation/memory.</summary>
public sealed record ContextHints
{
    /// <summary>E.g. "son has sports Wednesday".</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}
