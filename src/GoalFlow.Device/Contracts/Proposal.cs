using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Device → cloud (<c>type: "proposal"</c>): a mid-flight adaptation. Sent
/// when the ChangeWatcher deems a world change material and the re-planned
/// loop produces a new side-effect. <c>task_status</c> is "adapting".
/// <code>
/// { "type":"proposal","goal_id":"meal-2026-w29","correlation_id":"evt-014",
///   "task_status":"adapting",
///   "payload":{ "proposal_id":"p7","action":"add_prep_task",
///     "detail":"marinate Wed's chicken on Tue night",
///     "trigger":"calendar: son football Wed 18:00 — prep window shrinks",
///     "requires_approval":true } }
/// </code>
/// </summary>
public sealed record Proposal
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Proposal;

    [JsonPropertyName("goal_id")]
    public required string GoalId { get; init; }

    /// <summary>Dedupe key; the approval message echoes it back, e.g. "evt-014".</summary>
    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    /// <summary>See <see cref="TaskStatuses"/>; normally "adapting".</summary>
    [JsonPropertyName("task_status")]
    public required string TaskStatus { get; init; }

    [JsonPropertyName("payload")]
    public required ProposalItem Payload { get; init; }
}
