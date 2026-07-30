using System.Text.Json.Nodes;

namespace GoalFlow.Device.Harness;

/// <summary>
/// Narrows a dispatch's <c>constraints.hard</c> against the world BEFORE it is armed.
///
/// <para>
/// WHY THIS SEAM EXISTS (v6-M3). Some policy is only half a number until the device
/// looks at its own state. The account sets a household ENVELOPE ("$600 a month");
/// what a given goal may actually spend is that envelope minus what has already been
/// spent — and only the device knows the second half. Per-goal caps alone cannot stop
/// two goals spending the same money: a $200 party and a $120 grocery week each fit
/// their own ceiling and together blow the month.
/// </para>
///
/// <para>
/// THE INVARIANT IT PROTECTS. <see cref="SafetyRule"/> promises that rules read
/// <c>constraints.hard</c> and nothing else — that is what makes the gate auditable.
/// A rule that reached into <c>budget.json</c> mid-evaluation would quietly break it.
/// So the arithmetic happens HERE, once, in a resolution step, and what gets armed is
/// a plain hard block the rules can read exactly as before.
/// </para>
///
/// <para>
/// It is the product's job because the world is the product's: the harness must not
/// know that a household's spending lives in a document called "budget". A product
/// with no resolver simply arms what it was dispatched.
/// </para>
/// </summary>
public interface IPolicyResolver
{
    /// <summary>
    /// The effective constraints for this goal. Implementations MUST NOT mutate
    /// <paramref name="dispatched"/>, and may only make policy STRICTER — the
    /// resolution step narrows a ceiling, it never raises one.
    /// </summary>
    Task<JsonObject> ResolveAsync(JsonObject dispatched, CancellationToken ct = default);
}
