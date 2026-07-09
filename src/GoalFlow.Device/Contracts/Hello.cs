using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Handshake, device → cloud. The device opens ONE outbound WebSocket to the
/// cloud hub and sends this frame to register.
/// <code>{ "type":"hello", "role":"device" }</code>
/// </summary>
public sealed record Hello
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Hello;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "device";
}

/// <summary>
/// Handshake acknowledgement, cloud → device.
/// <code>{ "type":"hello_ack", "role":"device", "session_id":"..." }</code>
/// </summary>
public sealed record HelloAck
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.HelloAck;

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }
}
