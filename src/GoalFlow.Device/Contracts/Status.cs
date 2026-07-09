using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Device → cloud (<c>type: "status"</c>): lifecycle progress note.
/// <code>{ "type":"status","goal_id":"meal-2026-w29","correlation_id":"disp-001",
///   "task_status":"executing","payload":{"note":"..."} }</code>
/// </summary>
public sealed record StatusMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Status;

    [JsonPropertyName("goal_id")]
    public required string GoalId { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    /// <summary>See <see cref="TaskStatuses"/>.</summary>
    [JsonPropertyName("task_status")]
    public required string TaskStatus { get; init; }

    [JsonPropertyName("payload")]
    public required StatusPayload Payload { get; init; }
}

/// <summary>Payload of <see cref="StatusMessage"/>.</summary>
public sealed record StatusPayload
{
    [JsonPropertyName("note")]
    public required string Note { get; init; }
}
