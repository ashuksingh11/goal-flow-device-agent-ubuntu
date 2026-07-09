using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// A frozen side-effect awaiting human approval.
/// <para>
/// INVARIANT: the device sends side-effects as PROPOSALS, not actions — it
/// executes nothing until an approval returns via the cloud.
/// </para>
/// Shared shape: appears (a) inside <c>plan_ready.payload.proposals[]</c>
/// with <see cref="Items"/>/<see cref="Reason"/> populated, and (b) as the
/// payload of a standalone <c>proposal</c> (adaptation) message with
/// <see cref="Detail"/>/<see cref="Trigger"/> populated. Unused fields are
/// null and omitted from JSON.
/// </summary>
public sealed record ProposalItem
{
    /// <summary>Stable id used by the approval round-trip, e.g. "p1".</summary>
    [JsonPropertyName("proposal_id")]
    public required string ProposalId { get; init; }

    /// <summary>Effect verb, e.g. "add_to_shopping_list", "add_prep_task".</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>Items acted on (shopping-list style effects), e.g. ["bell peppers","lentils","yogurt"].</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<string>? Items { get; init; }

    /// <summary>Why the effect is needed (plan_ready style), e.g. "needed for Tue &amp; Thu dishes".</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Human-readable effect detail (adaptation style), e.g. "marinate Wed's chicken on Tue night".</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>What world change triggered it, e.g. "calendar: son football Wed 18:00 — prep window shrinks".</summary>
    [JsonPropertyName("trigger")]
    public string? Trigger { get; init; }

    /// <summary>Always true under autonomy "propose_all".</summary>
    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; init; } = true;
}
