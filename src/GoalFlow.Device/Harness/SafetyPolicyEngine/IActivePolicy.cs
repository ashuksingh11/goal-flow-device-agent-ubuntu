using System.Text.Json.Nodes;

namespace GoalFlow.Device.Harness;

/// <summary>
/// READ-ONLY access to the hard constraints armed for the goal the current async
/// flow belongs to. Implemented by <see cref="SafetyFilter"/>.
///
/// <para>
/// WHY THIS EXISTS (v6). A capability plugin sometimes needs to REPORT a policy
/// value it must not enforce: the Budget module tells the planner what the ceiling
/// is so it can plan under it, while the SafetyFilter is the thing that actually
/// blocks an over-spend. Before v6 the plugin got that number from the device's own
/// data/budget.json — a second copy of the cloud's <c>budget_cap</c>, hand-synced,
/// and the copy the planner read was not the copy anything enforced.
/// </para>
///
/// <para>
/// It is an interface, not the filter itself, so the dependency reads the right way
/// round: the product pack asks "what policy is armed?" without taking a reference
/// to the enforcement engine, and nothing in a plugin can reach a mutation path.
/// </para>
/// </summary>
public interface IActivePolicy
{
    /// <summary>A COPY of the armed constraints.hard, or null outside a goal scope.</summary>
    JsonObject? ActiveHard();
}
