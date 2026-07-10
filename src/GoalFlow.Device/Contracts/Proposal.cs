namespace GoalFlow.Device.Contracts;

/// <summary>
/// Generic ADAPTATION proposal, device → cloud → ui (<c>type: "proposal"</c>).
/// Emitted by Monitor &amp; Adapt when a MATERIAL world change makes part of the
/// approved plan wrong (an ingredient expired, a guest RSVP'd an allergy, ...).
/// Distinct from <see cref="ProposalItem"/>, which rides inside plan_ready.
/// </summary>
public sealed record Proposal
{
    public string Type { get; init; } = MessageTypes.Proposal;

    public required string GoalId { get; init; }

    public string? CorrelationId { get; init; }

    public string TaskStatus { get; init; } = TaskStatuses.Adapting;

    public required AdaptationPayload Payload { get; init; }
}

public sealed record AdaptationPayload
{
    /// <summary>E.g. "a1" — the id the subsequent approval decision references.</summary>
    public required string ProposalId { get; init; }

    /// <summary>Short action label, e.g. "swap Thursday dinner".</summary>
    public required string Action { get; init; }

    /// <summary>What changes, in full.</summary>
    public string? Detail { get; init; }

    /// <summary>The material world change that triggered this, e.g. "spinach expired".</summary>
    public required string Trigger { get; init; }

    /// <summary>See <see cref="ApprovalTiers"/>.</summary>
    public required string Tier { get; init; }

    public bool RequiresApproval { get; init; } = true;
}
