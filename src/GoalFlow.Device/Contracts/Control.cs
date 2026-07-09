using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Cloud -> device operator control message for virtual-clock demos.
/// </summary>
public sealed record Control
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Control;

    [JsonPropertyName("goal_id")]
    public required string GoalId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

public static class ControlCommands
{
    public const string AdvanceDay = "advance_day";
    public const string Reset = "reset";
}
