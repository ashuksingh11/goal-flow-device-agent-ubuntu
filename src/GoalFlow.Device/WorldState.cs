using System.Text.Json.Serialization;

namespace GoalFlow.Device;

/// <summary>
/// The coherent local world model produced by the Grounding harness: adapter
/// outputs (inventory, calendar, recipes, shopping list, reminders)
/// normalized into ONE object the planner and gates consume.
/// <para>
/// INVARIANT: the device is the sole authority on this state. Nothing outside
/// the device reads or writes it directly; the cloud only ever sees contract
/// messages derived from it.
/// </para>
/// </summary>
public sealed record WorldState
{
    /// <summary>Virtual-clock timestamp (from IClock) at which this snapshot was assembled.</summary>
    public required DateTimeOffset AsOf { get; init; }

    public required IReadOnlyList<InventoryItem> Inventory { get; init; }

    /// <summary>Calendar events falling inside (or overlapping) the contract time window.</summary>
    public required IReadOnlyList<CalendarEvent> Calendar { get; init; }

    /// <summary>Recipes the planner may choose from.</summary>
    public required IReadOnlyList<Recipe> Recipes { get; init; }

    public required IReadOnlyList<ShoppingListEntry> ShoppingList { get; init; }

    public required IReadOnlyList<Reminder> Reminders { get; init; }

    /// <summary>
    /// Derived view: inventory items expiring within the contract window,
    /// soonest first — the "reduce_waste" signal for the planner.
    /// </summary>
    public IReadOnlyList<InventoryItem> ExpiringSoon { get; init; } = [];
}

/// <summary>One item in the (mock) fridge/pantry inventory.</summary>
public sealed record InventoryItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("quantity")]
    public required double Quantity { get; init; }

    [JsonPropertyName("unit")]
    public required string Unit { get; init; }

    /// <summary>E.g. "vegetable", "dairy", "grain".</summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    /// <summary>ISO date the item expires; null = non-perishable.</summary>
    [JsonPropertyName("expires_on")]
    public string? ExpiresOn { get; init; }
}

/// <summary>One (mock) family-calendar event.</summary>
public sealed record CalendarEvent
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>ISO date, e.g. "2026-07-15".</summary>
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    /// <summary>Local start time "HH:mm".</summary>
    [JsonPropertyName("start")]
    public required string Start { get; init; }

    /// <summary>Local end time "HH:mm".</summary>
    [JsonPropertyName("end")]
    public string? End { get; init; }

    [JsonPropertyName("attendee")]
    public string? Attendee { get; init; }
}

/// <summary>One (mock) recipe the RulesPlanner can select.</summary>
public sealed record Recipe
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("ingredients")]
    public required IReadOnlyList<string> Ingredients { get; init; }

    /// <summary>Attribute tags matched against soft preferences, e.g. ["vegetarian","more_protein"].</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Substances relevant to hard constraints, e.g. ["dairy","gluten"]. Safety-gate input.</summary>
    [JsonPropertyName("contains")]
    public IReadOnlyList<string> Contains { get; init; } = [];

    /// <summary>Active prep+cook time; drives busy-evening selection.</summary>
    [JsonPropertyName("prep_minutes")]
    public int PrepMinutes { get; init; }
}

/// <summary>One entry on the (mock) shopping list actuator.</summary>
public sealed record ShoppingListEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("item")]
    public required string Item { get; init; }

    /// <summary>Why it was added (traceability back to a proposal).</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Correlation id of the approval that caused it (idempotency key).</summary>
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}

/// <summary>One entry in the (mock) reminders actuator.</summary>
public sealed record Reminder
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>ISO date the reminder fires.</summary>
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    /// <summary>Local time "HH:mm" (fired off the virtual clock, never wall-clock).</summary>
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}
