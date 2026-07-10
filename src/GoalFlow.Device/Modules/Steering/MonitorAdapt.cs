using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;

namespace GoalFlow.Device.Modules.Steering;

/// <summary>
/// HARNESS MODULE: Monitor &amp; Adapt.
/// After a plan is approved, the world keeps moving (day advances, an item
/// expires early, a guest RSVPs an allergy). This module compares fresh world
/// state against the plan's assumptions, applies the MATERIALITY POLICY
/// (deterministic code — not the LLM — decides whether a change matters), and
/// when material, produces a scoped adaptation <see cref="Proposal"/> that
/// re-plans ONLY the affected slice.
/// </summary>
public sealed class MonitorAdapt
{
    private readonly IClock _clock;
    private readonly MaterialityPolicy _policy;
    private readonly ILogger<MonitorAdapt> _logger;

    public MonitorAdapt(IClock clock, MaterialityPolicy policy, ILogger<MonitorAdapt> logger)
    {
        _clock = clock;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Re-reads world state (via the capability plugins) as of the clock's
    /// current date and diffs it against the approved plan's assumptions.
    /// Returns detected changes, each pre-classified by the materiality policy.
    /// </summary>
    public Task<IReadOnlyList<WorldChange>> ObserveAsync(string goalId, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>
    /// For a material change: build the adaptation proposal (tier "adapt"-level
    /// consent = requires approval) — a scoped re-plan, e.g. "swap Thursday's
    /// dinner; spinach expired". Non-material changes yield null (logged only).
    /// </summary>
    public Task<Proposal?> ProposeAdaptationAsync(string goalId, WorldChange change, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");
}

/// <summary>
/// Deterministic materiality rules — what is worth waking the user for.
/// E.g. an ingredient a PLANNED dish depends on expiring is material; a pantry
/// item no dish uses expiring is not.
/// </summary>
public sealed class MaterialityPolicy
{
    /// <summary>True when the change invalidates part of the approved plan.</summary>
    public bool IsMaterial(WorldChange change)
        => throw new NotImplementedException("v2-M0 design skeleton");
}

/// <summary>One observed world-state delta.</summary>
public sealed record WorldChange
{
    /// <summary>E.g. "inventory.expired", "calendar.event_added", "guest.rsvp_changed".</summary>
    public required string Kind { get; init; }

    /// <summary>Human-readable description — becomes the proposal's trigger.</summary>
    public required string Description { get; init; }

    /// <summary>Plan-item ids this change touches (the re-plan slice).</summary>
    public IReadOnlyList<string> AffectedPlanItems { get; init; } = [];

    /// <summary>Filled by the materiality policy.</summary>
    public bool Material { get; init; }
}
