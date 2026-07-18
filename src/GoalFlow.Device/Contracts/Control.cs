namespace GoalFlow.Device.Contracts;

/// <summary>
/// Demo/ops control, ui → cloud → device (<c>type: "control"</c>).
/// Drives the GENERIC clock: <c>set_date</c> pins the SimulatedClock to an ISO
/// date, <c>advance_day</c> steps it forward, <c>reset</c> restores the mock
/// world + real clock. The device NEVER hardcodes a date.
///
/// <para>
/// v3.2: <see cref="GoalId"/> is OPTIONAL. A clock command with no goal_id (empty) is
/// a WORLD-level tick — it advances the global clock once and fans out to every active
/// goal (see <c>HandleWorldControlAsync</c>). A goal_id scopes the older per-goal path
/// (a <c>trigger_event</c>, still supported).
/// </para>
/// </summary>
public sealed record Control
{
    public string Type { get; init; } = MessageTypes.Control;

    /// <summary>Empty = a world-level clock command that fans out to all active goals.</summary>
    public string GoalId { get; init; } = "";

    /// <summary>One of <see cref="ControlCommands"/>.</summary>
    public required string Command { get; init; }

    /// <summary>Optional direct event id echo/fallback; normal wire shape carries this in <see cref="Payload"/>.</summary>
    public string? EventId { get; init; }

    public ControlPayload? Payload { get; init; }
}

public sealed record ControlPayload
{
    /// <summary>ISO date for <see cref="ControlCommands.SetDate"/>.</summary>
    public string? Date { get; init; }

    /// <summary>Daily demo event id for <see cref="ControlCommands.TriggerEvent"/>.</summary>
    public string? EventId { get; init; }
}

public static class ControlCommands
{
    public const string AdvanceDay = "advance_day";
    public const string Reset = "reset";
    public const string SetDate = "set_date";
    public const string TriggerEvent = "trigger_event";
}
