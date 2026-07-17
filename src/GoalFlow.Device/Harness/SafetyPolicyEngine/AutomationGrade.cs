using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harness;

/// <summary>
/// How much consent an action needs. The v3 architecture docs call these
/// "automation grades"; v2 called the same axis "approval tiers".
///
/// <para>
/// They ARE the same axis, so v3 unifies on grades rather than carrying two
/// vocabularies — with one exception: <c>adapt</c> was never a consent level at
/// all. It describes where a proposal came FROM (the sustain loop noticed the
/// world moved), not how much consent it needs. It becomes a proposal ORIGIN;
/// an adaptation carries the grade of the effect it actually performs.
/// </para>
///
/// <para>
/// Ordering is deliberate and load-bearing: the enum increases in strictness, so
/// the policy ratchet (config may only make a grade stricter, never looser) is a
/// plain <c>&gt;</c> comparison. Do not reorder.
/// </para>
/// </summary>
public enum AutomationGrade
{
    /// <summary>Automatic. Reversible and cheap — just do it, and say so. (v2 tier: auto)</summary>
    A0 = 0,

    /// <summary>Low-risk; rides the batched plan approval. (v2 tier: light)</summary>
    A1 = 1,

    /// <summary>Explicit approval required; never executes before it. (v2 tier: firm)</summary>
    A2 = 2,

    /// <summary>
    /// Prohibited. Not "needs a really good approval" — there is NO approval path.
    /// The capability may still exist and be advertised (the product can do it;
    /// the agent may not), which is what lets the device give a real refusal
    /// instead of pretending the tool doesn't exist. NEW in v3; no v2 equivalent.
    /// </summary>
    AX = 3
}

/// <summary>Maps between v3 grades and the v2 tier strings still on the wire.</summary>
public static class Grades
{
    /// <summary>
    /// The grade a <see cref="SideEffectAttribute"/> tier declares. Read-only
    /// functions have no tier and no grade (null) — they are not actions.
    /// </summary>
    public static AutomationGrade? FromTier(string? tier) => tier switch
    {
        ApprovalTiers.Auto => AutomationGrade.A0,
        ApprovalTiers.Light => AutomationGrade.A1,
        ApprovalTiers.Firm => AutomationGrade.A2,
        // Not a consent level — an origin. A proposal tagged 'adapt' carries the
        // grade of its underlying effect; see AutomationGrade's remarks.
        ApprovalTiers.Adapt => null,
        _ => null
    };

    /// <summary>
    /// The wire tier for a grade. AX has none by construction: a prohibited
    /// action is never proposed, so it never reaches the wire as a proposal.
    /// (Grades themselves go on the wire in M6 with the rest of contract v3 —
    /// until then this keeps the advertisement byte-identical.)
    /// </summary>
    public static string? ToTier(AutomationGrade grade) => grade switch
    {
        AutomationGrade.A0 => ApprovalTiers.Auto,
        AutomationGrade.A1 => ApprovalTiers.Light,
        AutomationGrade.A2 => ApprovalTiers.Firm,
        _ => null
    };

    /// <summary>Parses a policy-config grade name; throws with context on a typo.</summary>
    public static AutomationGrade Parse(string value, string context)
        => Enum.TryParse<AutomationGrade>(value, ignoreCase: true, out var grade)
            ? grade
            : throw new InvalidOperationException(
                $"{context}: '{value}' is not a valid automation grade (expected A0, A1, A2 or AX).");
}
