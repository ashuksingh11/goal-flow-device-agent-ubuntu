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

    /// <summary>
    /// Verdict of the Pre-check Engine — whether the WORLD was ready (v3-M3).
    /// Distinct from <see cref="Safety"/> on purpose: safety says "never", a
    /// precheck says "not yet". Absent when nothing was checked.
    /// </summary>
    public PrecheckVerdict? Precheck { get; init; }

    /// <summary>Headline outcomes for the UI, e.g. {"label":"waste","value":"-2 items"}.</summary>
    public IReadOnlyList<ImpactItem> Impact { get; init; } = [];

    /// <summary>Presenter-fired demo event chips for the meal-plan week.</summary>
    public IReadOnlyList<DemoEvent>? DemoEvents { get; init; }

    /// <summary>One-paragraph natural-language rationale for the plan.</summary>
    public string? Explanation { get; init; }
}

/// <summary>Small display catalog entry for a presenter-fired demo event.</summary>
public sealed record DemoEvent
{
    public required string Id { get; init; }

    public int Day { get; init; }

    public required string Label { get; init; }

    public required string Title { get; init; }

    public required string Kind { get; init; }

    public int Order { get; init; }
}

/// <summary>One generic plan step (a dinner, a prep task, an errand, ...).</summary>
public sealed record PlanItem
{
    /// <summary>Stable id within the plan, e.g. "s1".</summary>
    public required string Id { get; init; }

    /// <summary>Stable 1-based plan-day index; the source of truth for meal-week ordering.</summary>
    public int Day { get; init; }

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

/// <summary>
/// Whether the WORLD was ready (v3-M3). Deliberately separate from
/// <see cref="SafetyVerdict"/>: safety is a refusal ("never"), a precheck is a
/// delay ("not yet"). The UI should say which — one means the house rules
/// stopped it, the other means something is unplugged and it will resume.
/// </summary>
public sealed record PrecheckVerdict
{
    /// <summary>True when nothing failed. Warnings do not block.</summary>
    public required bool Ok { get; init; }

    public IReadOnlyList<PrecheckResultDto> Results { get; init; } = [];
}

/// <summary>One probe's answer, on the wire.</summary>
public sealed record PrecheckResultDto
{
    /// <summary>The check as bound in the pack's config, e.g. "appliance_online:oven".</summary>
    public required string Id { get; init; }

    /// <summary>"pass" | "warn" | "fail" | "skipped".</summary>
    public required string Status { get; init; }

    /// <summary>What to DO about it — the sentence the user actually needs.</summary>
    public string? Detail { get; init; }
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

/// <summary>
/// A MINIMAL plan diff produced by the scoped daily-adaptation LLM call — the
/// tokens-lean alternative to re-emitting the whole week. It rides inside
/// <see cref="AdaptationPayload"/> as a PREVIEW of the proposed change, and once
/// approved is applied to the active plan (the result ships back in
/// <see cref="StatusPayload.UpdatedPlan"/>). Only the affected rows appear here.
/// </summary>
public sealed record PlanPatch
{
    /// <summary>Rows to insert or replace, matched by <see cref="PlanItem.Id"/>
    /// (a swapped dinner, a new prep task). Reusing an existing id replaces it.</summary>
    public IReadOnlyList<PlanItem> Upsert { get; init; } = [];

    /// <summary>Plan-item ids to drop from the plan.</summary>
    public IReadOnlyList<string> Remove { get; init; } = [];

    /// <summary>Impact badges to add/replace on the plan card, e.g. {"waste":"-2 items"}.</summary>
    public IReadOnlyList<ImpactItem> ImpactDelta { get; init; } = [];

    /// <summary>One-line rationale, e.g. "Swapped Wed dinner to use the spinach before it expires."</summary>
    public string? Rationale { get; init; }
}
