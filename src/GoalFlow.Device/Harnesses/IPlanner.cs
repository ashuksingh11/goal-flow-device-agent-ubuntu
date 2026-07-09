using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Decide-phase harness: contract + world-state + constraints → candidate
/// plan. SWAPPABLE via config/DI:
/// <list type="bullet">
///   <item><see cref="RulesPlanner"/> — default, deterministic, demo-safe.</item>
///   <item><see cref="LlmPlanner"/> — Semantic Kernel + OpenRouter.</item>
///   <item><see cref="ScriptedPlanner"/> — canned fallback (M1 / offline demo).</item>
/// </list>
/// <para>
/// INVARIANT: the planner NEVER runs the safety check. It may read soft
/// constraints as hints, but hard-constraint enforcement belongs exclusively
/// to <see cref="ISafetyGate"/> — "LLM plans, code checks."
/// </para>
/// </summary>
public interface IPlanner
{
    /// <summary>
    /// Produces a candidate plan (per-day dishes + frozen side-effect
    /// proposals). The result is UNGATED — the pipeline passes it to the
    /// safety gate next.
    /// </summary>
    Task<CandidatePlan> CreatePlanAsync(
        Dispatch contract,
        WorldState world,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The planner's output, pre-gate. Reuses the wire shapes so plan_ready
/// assembly is a straight projection.
/// </summary>
public sealed record CandidatePlan
{
    /// <summary>One entry per scoped day.</summary>
    public required IReadOnlyList<PlanItem> Plan { get; init; }

    /// <summary>Side-effects frozen as proposals (never executed directly).</summary>
    public required IReadOnlyList<ProposalItem> Proposals { get; init; }

    /// <summary>Which planner produced it ("rules" | "llm" | "scripted") — for the trace.</summary>
    public required string PlannerId { get; init; }
}
