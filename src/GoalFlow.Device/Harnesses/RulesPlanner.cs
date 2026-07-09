using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Default planner: deterministic, demo-safe, no network. Build effort: FULL
/// logic later.
/// <para>Intended selection heuristics (implementation phase):</para>
/// <list type="number">
///   <item>Prefer recipes consuming <see cref="WorldState.ExpiringSoon"/> items (reduce_waste).</item>
///   <item>Honor soft preferences (prefer tags) and avoid soft dislikes.</item>
///   <item>On days with evening calendar events, pick low <c>prep_minutes</c> recipes.</item>
///   <item>Diff recipe ingredients vs inventory → missing items become ONE
///         add_to_shopping_list proposal.</item>
/// </list>
/// Note: it may use soft constraints as scoring hints but performs NO
/// hard-constraint check — that is the safety gate's job.
/// </summary>
public sealed class RulesPlanner : IPlanner
{
    private readonly ITrace _trace;

    public RulesPlanner(ITrace trace) => _trace = trace;

    public Task<CandidatePlan> CreatePlanAsync(
        Dispatch contract,
        WorldState world,
        CancellationToken cancellationToken = default) =>
        // TODO: implement the deterministic heuristics documented above;
        // PlannerId = "rules"; trace the scoring decisions.
        throw new NotImplementedException("Design stub.");
}
