namespace GoalFlow.Device.Contracts;

/// <summary>
/// User decisions, ui → cloud → device (<c>type: "approval"</c>).
/// Consumed by the ApprovalCoordinator: approved proposals move
/// pending → approved → executed; rejected ones are dropped.
/// </summary>
public sealed record Approval
{
    public string Type { get; init; } = MessageTypes.Approval;

    public required string GoalId { get; init; }

    public string? CorrelationId { get; init; }

    public required ApprovalPayload Payload { get; init; }
}

public sealed record ApprovalPayload
{
    public required IReadOnlyList<ApprovalDecision> Decisions { get; init; }
}

/// <summary>One decision against a <see cref="ProposalItem.ProposalId"/>.</summary>
public sealed record ApprovalDecision
{
    public required string ProposalId { get; init; }

    public required bool Approved { get; init; }
}
