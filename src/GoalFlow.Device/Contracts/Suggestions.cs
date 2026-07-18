namespace GoalFlow.Device.Contracts;

/// <summary>
/// The device's current proactive-suggestion list, device → cloud
/// (<c>type: "suggestions"</c>).
///
/// <para>
/// THE ONE GOAL-LESS FRAME. Every other thing the device sends is about a goal already
/// dispatched — a plan, a proposal, a status tick. This is the device looking at its
/// own local state with no goal in flight and saying "you could do this": food about to
/// expire, stock running low. Only the device sees the fridge, so only the device can
/// raise one.
/// </para>
///
/// <para>
/// A suggestion is NOT a goal. The cloud turns an accepted one into an ordinary
/// <c>user_goal</c>; nothing here acts on its own. Deterministic to build (a scan, not
/// an LLM) so the board shows the same suggestions every run.
/// </para>
/// </summary>
public sealed record SuggestionsMessage
{
    public string Type { get; init; } = MessageTypes.Suggestions;

    public required IReadOnlyList<SuggestionItem> Items { get; init; }
}

/// <summary>One proactive suggestion: a goal the device thinks is worth doing.</summary>
public sealed record SuggestionItem
{
    public required string Id { get; init; }

    /// <summary>The scan that produced it — "expiring" | "restock". Drives the card glyph.</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    public string Subtitle { get; init; } = "";

    public string Detail { get; init; } = "";

    /// <summary>The goal text submitted verbatim as a user_goal if this is accepted.</summary>
    public required string GoalText { get; init; }
}
