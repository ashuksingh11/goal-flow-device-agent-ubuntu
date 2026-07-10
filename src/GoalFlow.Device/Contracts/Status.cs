namespace GoalFlow.Device.Contracts;

/// <summary>
/// Lifecycle/heartbeat frame, device → cloud → ui (<c>type: "status"</c>).
/// Reports the task-status plus what executed and where the (generic) clock is.
/// </summary>
public sealed record Status
{
    public string Type { get; init; } = MessageTypes.Status;

    public required string GoalId { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>One of <see cref="TaskStatuses"/>.</summary>
    public required string TaskStatus { get; init; }

    public required StatusPayload Payload { get; init; }
}

public sealed record StatusPayload
{
    /// <summary>Human day label within the window, e.g. "Wed" (optional).</summary>
    public string? Day { get; init; }

    /// <summary>Current clock date (ISO) — real today, or the simulated date after set_date/advance_day.</summary>
    public string? SimDate { get; init; }

    /// <summary>Whether the last observed world change was material (triggered adaptation).</summary>
    public bool Material { get; init; }

    /// <summary>Proposal ids executed so far.</summary>
    public IReadOnlyList<string> Executed { get; init; } = [];

    public string? Note { get; init; }
}
