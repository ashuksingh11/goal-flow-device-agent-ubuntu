using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Cloud → device (<c>type: "approval"</c>): the human decision(s), relayed
/// by the cloud. <c>correlation_id</c> correlates the decision back to the
/// proposal it answers (dedupe on reconnect).
/// </summary>
public sealed record Approval
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Approval;

    [JsonPropertyName("goal_id")]
    public required string GoalId { get; init; }

    /// <summary>Echoes the correlation id of the message that carried the proposal(s).</summary>
    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("payload")]
    public required ApprovalPayload Payload { get; init; }
}

/// <summary>Payload of <see cref="Approval"/>.</summary>
public sealed record ApprovalPayload
{
    [JsonPropertyName("decisions")]
    public required IReadOnlyList<ApprovalDecision> Decisions { get; init; }
}

/// <summary>One per-proposal verdict, e.g. { "proposal_id":"p7","approved":true }.</summary>
public sealed record ApprovalDecision
{
    [JsonPropertyName("proposal_id")]
    public required string ProposalId { get; init; }

    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }
}
