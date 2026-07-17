namespace GoalFlow.Device.Harness;

/// <summary>
/// Marks a capability plugin as NOT AVAILABLE for planning: a declared extension
/// point whose methods throw. Unavailable plugins are still ADVERTISED in the
/// capabilities message — an extension point should be visible — but they are
/// excluded from the planner's grounding tool set, so the model is never handed
/// a tool that throws.
///
/// <para>
/// This is why it exists at all: the read functions of the stub plugins look
/// exactly like real ones to reflection. Deriving the tool set as "every
/// non-side-effecting function" yields 17 functions; the planner has only ever
/// been given 13. The four extra are FamilyProfiles.GetProfiles/GetMember and
/// Budget.GetBudgetStatus/EstimateCost — reads that throw NotImplementedException.
/// Availability is what makes discovery reproduce the hand-written list exactly.
/// </para>
///
/// <para>
/// An ATTRIBUTE rather than config, deliberately: it sits in the same file as
/// the NotImplementedException it describes, so implementing a stub means
/// writing the bodies and deleting this line in the file you already have open.
/// A config row claiming a stub is available while the code still throws is a
/// live crash; this cannot drift.
/// </para>
///
/// <para>
/// Scope: STATIC "not built yet". Runtime unavailability — the camera is
/// offline, SmartThings is unreachable — is a different axis and belongs to the
/// Pre-check Engine (M3), not here.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UnavailableAttribute : Attribute
{
    public UnavailableAttribute(string reason) => Reason = reason;

    /// <summary>Why it is unavailable, surfaced in logs and (later) to the cloud.</summary>
    public string Reason { get; }
}
