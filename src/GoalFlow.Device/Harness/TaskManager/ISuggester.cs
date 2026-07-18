using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harness;

/// <summary>
/// Scans local state and proposes goals the user hasn't asked for yet.
///
/// <para>
/// THE PROACTIVE SEAM (v3-M8). Every other device capability is REACTIVE — it answers a
/// dispatched goal. A suggester is the opposite: it looks at the world with no goal in
/// flight ("food is about to expire", "staples are low") and raises a suggestion. Only
/// the device sees the fridge, so only the device can raise one.
/// </para>
///
/// <para>
/// DETERMINISTIC, not an LLM — a scan, so the board shows the same suggestions every
/// run and a demo is repeatable. And it never acts: a suggestion carries the goal TEXT
/// it would become, and only a person accepting it turns "you could do this" into a
/// dispatched goal. What is worth suggesting is product judgement (a restocked pantry
/// nobody cooks with is not), exactly like <see cref="IDomainObserver"/>'s materiality —
/// so the harness owns the mechanism and the product owns the scan.
/// </para>
/// </summary>
public interface ISuggester
{
    Task<IReadOnlyList<SuggestionItem>> ScanAsync(CancellationToken ct = default);
}
