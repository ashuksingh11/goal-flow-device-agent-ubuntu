namespace GoalFlow.Device.Contracts;

/// <summary>
/// A GLOBAL world tick, device → cloud → ui (<c>type: "day_advanced"</c>).
///
/// <para>
/// Emitted once per goal-less <c>advance_day</c>/<c>set_date</c>/<c>reset</c> control:
/// the sim clock moved for the WHOLE world, so this summarises the world events that
/// happened on the new day and which goals each one touched. The board renders it as
/// the "what happened today" card; the per-goal <c>status</c>/<c>proposal</c> frames
/// that ride alongside update each goal card. A quiet day carries an empty
/// <see cref="Events"/> list.
/// </para>
/// </summary>
public sealed record DayAdvanced
{
    public string Type { get; init; } = MessageTypes.DayAdvanced;

    /// <summary>The new simulated date (ISO) after the tick.</summary>
    public required string SimDate { get; init; }

    /// <summary>1-based sim day, from the earliest active goal's window start (0 if none).</summary>
    public int Day { get; init; }

    public IReadOnlyList<DayEvent> Events { get; init; } = [];
}

/// <summary>One world event that happened on the advanced day.</summary>
public sealed record DayEvent
{
    public required string Id { get; init; }

    /// <summary>Human-readable headline, e.g. "Day 3 - football practice added Wed".</summary>
    public required string Title { get; init; }

    /// <summary>The change kind, e.g. "calendar.event_overlap".</summary>
    public string? Kind { get; init; }

    public string? Summary { get; init; }

    /// <summary>Goals this event was material to (empty = it happened but touched no active goal).</summary>
    public IReadOnlyList<string> GoalIds { get; init; } = [];
}
