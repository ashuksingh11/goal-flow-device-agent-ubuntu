using System.Text.Json.Nodes;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// The GENERIC Task Contract, cloud → device (<c>type: "dispatch"</c>).
/// Domain-agnostic by design: <see cref="Domain"/> names the use case
/// ("meal_plan", "guest_dinner", ...); domain specifics ride in the free-form
/// <see cref="Scope"/> / <see cref="Context"/> objects and in whichever
/// capability modules the planner chooses to call.
/// <para>
/// INVARIANT: <c>constraints.hard</c> is the ONLY thing the Safety filter
/// enforces ("LLM plans, code checks"). Soft constraints are planner hints.
/// </para>
/// <para>
/// INVARIANT: <see cref="TimeWindow"/> dates are RELATIVE to real today —
/// the device resolves them against its generic <c>IClock</c>, never a
/// hardcoded anchor.
/// </para>
/// </summary>
public sealed record Dispatch
{
    public string Type { get; init; } = MessageTypes.Dispatch;

    /// <summary>Goal identity; carried on every task message.</summary>
    public required string GoalId { get; init; }

    /// <summary>Correlation/dedupe key echoed on device replies for this dispatch.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Use-case name, e.g. "meal_plan" or "guest_dinner".</summary>
    public required string Domain { get; init; }

    /// <summary>Structured objective distilled from the fuzzy user goal.</summary>
    public required string Objective { get; init; }

    /// <summary>What "done well" means, e.g. ["5 dinners planned", "waste reduced"].</summary>
    public IReadOnlyList<string> SuccessCriteria { get; init; } = [];

    public required TaskConstraints Constraints { get; init; }

    /// <summary>Domain-flexible scope object (meal: slot/days; guest: headcount/date...).</summary>
    public JsonObject? Scope { get; init; }

    public TimeWindow? TimeWindow { get; init; }

    /// <summary>Autonomy mode; v2 uses "tiered" (auto / light / firm proposals).</summary>
    public string Autonomy { get; init; } = AutonomyModes.Tiered;

    /// <summary>Free-form context the cloud relays (notes, memory snippets, ...).</summary>
    public JsonObject? Context { get; init; }
}

/// <summary>Hard + soft constraint blocks.</summary>
public sealed record TaskConstraints
{
    /// <summary>
    /// The safety policy — deterministic blockers, and the Safety filter's ONLY
    /// input. Free-form by contract (allergens, medical, dietary, budget_cap,
    /// quiet_hours, ...): the filter knows how to read the keys it enforces.
    /// </summary>
    public required JsonObject Hard { get; init; }

    /// <summary>Preferences — steer the planner, never gate input.</summary>
    public JsonObject? Soft { get; init; }
}

/// <summary>Inclusive ISO-8601 window, computed by the cloud RELATIVE to real today.</summary>
public sealed record TimeWindow
{
    public required string Start { get; init; }

    public required string End { get; init; }
}

/// <summary>Autonomy modes carried on the Task Contract.</summary>
public static class AutonomyModes
{
    /// <summary>Side-effects are proposed with a tier; firm ones pause for approval.</summary>
    public const string Tiered = "tiered";
}
