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
    [JsonPropertyName("executed")]
    public IReadOnlyList<ExecutedEffect>? Executed { get; init; }

    [JsonPropertyName("day")]
    public string? Day { get; init; }

    [JsonPropertyName("sim_date")]
    public string? SimDate { get; init; }

    [JsonPropertyName("note")]
    public required string Note { get; init; }

    [JsonPropertyName("material")]
    public bool? Material { get; init; }
}

/// <summary>One executed approved effect reported in a status payload.</summary>
public sealed record ExecutedEffect
{
    [JsonPropertyName("proposal_id")]
    public required string ProposalId { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }
}
