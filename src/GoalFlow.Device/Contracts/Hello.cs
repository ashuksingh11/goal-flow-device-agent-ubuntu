namespace GoalFlow.Device.Contracts;

/// <summary>
/// Registration frame, client → cloud (<c>type: "hello"</c>). The device opens
/// ONE outbound WebSocket to the cloud hub and identifies its role.
/// </summary>
public sealed record Hello
{
    public string Type { get; init; } = MessageTypes.Hello;

    /// <summary>"device" for this agent ("ui" for the UI client).</summary>
    public string Role { get; init; } = Roles.Device;
}

/// <summary>Cloud → client acknowledgement (<c>type: "hello_ack"</c>).</summary>
public sealed record HelloAck
{
    public string Type { get; init; } = MessageTypes.HelloAck;

    public required string Role { get; init; }

    public required string SessionId { get; init; }
}

/// <summary>Client roles known to the hub.</summary>
public static class Roles
{
    public const string Ui = "ui";
    public const string Device = "device";
}
