using System.Text.Json.Nodes;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// The generic plan + tiered proposals, device → cloud (<c>type: "plan_ready"</c>).
/// The cloud relays it to the UI as <c>present_plan</c> (adding
/// <c>payload.knew</c>, the personalization summary — cloud-side only).
/// </summary>
public sealed record PlanReady
{
    public string Type { get; init; } = MessageTypes.PlanReady;

    public required string GoalId { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Normally <see cref="TaskStatuses.AwaitingApproval"/>.</summary>
    public required string TaskStatus { get; init; }

    public required PlanReadyPayload Payload { get; init; }
}

public sealed record PlanReadyPayload
{
    /// <summary>The domain-agnostic plan the LLM produced.</summary>
    public required IReadOnlyList<PlanItem> Plan { get; init; }

    /// <summary>Every side-effecting action, frozen into tiered proposals.</summary>
    public required IReadOnlyList<ProposalItem> Proposals { get; init; }

    /// <summary>Verdict of the deterministic Safety filter over the whole run.</summary>
    public required SafetyVerdict Safety { get; init; }

    /// <summary>Headline outcomes for the UI, e.g. {"label":"waste","value":"-2 items"}.</summary>
    public IReadOnlyList<ImpactItem> Impact { get; init; } = [];

    /// <summary>One-paragraph natural-language rationale for the plan.</summary>
    public string? Explanation { get; init; }
}

/// <summary>One generic plan step (a dinner, a prep task, an errand, ...).</summary>
public sealed record PlanItem
{
    /// <summary>Stable id within the plan, e.g. "s1".</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Detail { get; init; }

    /// <summary>Optional ISO timestamp/date — RELATIVE to real today via the clock.</summary>
    public string? When { get; init; }

    /// <summary>Per-decision rationale chips, e.g. ["uses_expiring_spinach"].</summary>
    public IReadOnlyList<string> Why { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// A proposed side-effecting tool call. The LLM's pending call is frozen here
/// (module + function + args) with a tier; the ApprovalCoordinator holds it
/// until the matching <c>approval</c> decision arrives.
/// </summary>
public sealed record ProposalItem
{
    public required string ProposalId { get; init; }

    /// <summary>Human-readable action label, e.g. "add 5 items to the shopping list".</summary>
    public required string Action { get; init; }

    /// <summary>Capability module (SK plugin) name, e.g. "ShoppingList".</summary>
    public required string Module { get; init; }

    /// <summary>[KernelFunction] name, e.g. "Add".</summary>
    public required string Function { get; init; }

    /// <summary>The exact arguments the function will be invoked with on approval.</summary>
    public JsonObject? Args { get; init; }

    /// <summary>See <see cref="ApprovalTiers"/>.</summary>
    public required string Tier { get; init; }

    public string? Reason { get; init; }

    public bool RequiresApproval { get; init; } = true;
}

/// <summary>Outcome of the Safety filter's deterministic hard-constraint checks.</summary>
public sealed record SafetyVerdict
{
    /// <summary>"passed" or "blocked" (see <see cref="SafetyGates"/>).</summary>
    public required string Gate { get; init; }

    /// <summary>Human-readable violations when blocked; empty when passed.</summary>
    public IReadOnlyList<string> Violations { get; init; } = [];
}

public static class SafetyGates
{
    public const string Passed = "passed";
    public const string Blocked = "blocked";
}

/// <summary>A headline impact metric shown on the plan card.</summary>
public sealed record ImpactItem
{
    public required string Label { get; init; }

    public required string Value { get; init; }
}
