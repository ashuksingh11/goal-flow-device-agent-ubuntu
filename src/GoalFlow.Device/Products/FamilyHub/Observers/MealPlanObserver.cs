using System.Globalization;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// Watches the meal week (domain <c>meal_plan</c>).
///
/// <para>
/// Changes come from the DAILY WORLD-CHANGE FEED (<c>data/daily_events.json</c>):
/// one curated, believable real-world change per day — a fridge restock, an item
/// running out, a calendar clash, an extra guest, an appliance going down. Each
/// targets a deterministic Day N plan item, and the harness dedups it to exactly
/// once by its stable key.
/// </para>
///
/// <para>
/// MATERIALITY: every feed entry is material, and that is not laziness — the feed
/// IS the materiality decision, curated so the demo shows four quiet days and one
/// smart adaptation rather than a stream of noise. What is NOT material never
/// enters the feed in the first place.
/// </para>
/// </summary>
public sealed class MealPlanObserver : IDomainObserver
{
    private readonly IProductApiAdapter _store;
    private readonly IClock _clock;

    public MealPlanObserver(IProductApiAdapter store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public string Domain => "meal_plan";

    public string Hint => "planning the week's dinners — healthy eating, using up what is in the fridge";

    public async Task<JsonObject> CaptureAsync(CancellationToken ct = default)
        => new()
        {
            ["calendar"] = await _store.LoadResolvedAsync("calendar", ct),
            ["recipes"] = await _store.LoadResolvedAsync("recipes", ct),
            ["daily_events"] = await LoadFeedAsync(ct)
        };

    public IReadOnlyList<WorldChange> Observe(GoalRecord goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var feed = goal.WorldSnapshot["daily_events"]?["events"]?.AsArray() ?? [];

        return feed
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            // Reach-or-pass: fire on the first tick on/after the event's day, so
            // advancing several days at once doesn't skip it.
            .Where(ev => ev["date"]?.GetValue<string>() is { } d && string.CompareOrdinal(today, d) >= 0)
            .Select(ev => BuildChange(goal, ev))
            .ToArray();
    }

    public IReadOnlyList<DemoEvent>? DemoEvents(JsonObject snapshot)
        => (snapshot["daily_events"]?["events"]?.AsArray() ?? [])
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            .Select(ev => new DemoEvent
            {
                Id = ev["id"]?.GetValue<string>() ?? "",
                Day = ev["day"]?.GetValue<int>() ?? ev["order"]?.GetValue<int>() ?? 0,
                Label = ev["label"]?.GetValue<string>() ?? "",
                Title = ev["title"]?.GetValue<string>() ?? "",
                Kind = ev["kind"]?.GetValue<string>() ?? "world.change",
                Order = ev["order"]?.GetValue<int>() ?? int.MaxValue
            })
            .Where(ev => ev.Id.Length > 0)
            .OrderBy(ev => ev.Order)
            .ToArray();

    public WorldChange? TriggerEvent(GoalRecord goal, string eventId)
    {
        var ev = (goal.WorldSnapshot["daily_events"]?["events"]?.AsArray() ?? [])
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            .FirstOrDefault(e => string.Equals(e["id"]?.GetValue<string>(), eventId, StringComparison.Ordinal));

        return ev is null ? null : BuildChange(goal, ev);
    }

    /// <summary>One feed entry → a change aimed at a specific plan day.</summary>
    private WorldChange BuildChange(GoalRecord goal, JsonObject ev)
    {
        var evDate = ev["date"]?.GetValue<string>();
        var id = ev["id"]?.GetValue<string>() ?? evDate ?? "unknown";
        var requestedDay = ev["day"]?.GetValue<int>() ?? 1;
        var targetItem = FindTargetPlanItem(goal.Plan, requestedDay);
        var targetDay = targetItem?.Day > 0
            ? targetItem.Day
            : Math.Max(1, Math.Min(requestedDay, goal.Plan.Count));
        var affected = targetItem is null ? ["dinner"] : new[] { targetItem.Id };
        var summary = ev["summary"]?.GetValue<string>() ?? "A change occurred in the home.";
        var context = ev["context"]?.AsObject()?.DeepClone().AsObject() ?? new JsonObject();
        context["target_day"] = targetDay;
        if (targetItem is not null)
        {
            context["target_item_id"] = targetItem.Id;
            context["target_title"] = targetItem.Title;
        }

        var kind = ev["kind"]?.GetValue<string>() ?? "world.change";
        // The event's resolved fire date IS the target day's calendar date (the feed keeps
        // day_offset and target day in lockstep: day == day_offset + 1). Show that real date
        // rather than an opaque "Day N" ordinal (v4.2).
        var whenLabel = FormatWhen(evDate) ?? $"Day {targetDay}";
        return new WorldChange
        {
            // STABLE key — the feed keeps returning this event every day after its
            // date, so the key must not embed today or it would re-fire daily.
            Key = $"daily:{id}",
            Kind = kind,
            Description = $"{whenLabel} - {summary}",
            AffectedPlanItems = affected,
            TargetDay = targetDay,
            TargetItemId = targetItem?.Id,
            TargetTitle = targetItem?.Title,
            RecommendedAction = ev["steer"]?.GetValue<string>(),
            Steer = ev["steer"]?.GetValue<string>(),
            Context = context,
            Material = IsMaterial(kind)
        };
    }

    /// <summary>
    /// Which meal-week changes are worth waking the family for. The daily feed is
    /// curated, so every kind it can emit is material by construction; anything
    /// unrecognised is not, so a stray entry stays quiet rather than nagging.
    /// </summary>
    private static bool IsMaterial(string kind) => kind switch
    {
        "calendar.event_overlap" => true,
        "inventory.restocked" => true,
        "inventory.shortage" => true,
        "guest.headcount_added" => true,
        "appliance.unavailable" => true,
        "meal.lighter_requested" => true,
        _ => false
    };

    private static PlanItem? FindTargetPlanItem(IReadOnlyList<PlanItem> plan, int requestedDay)
        => plan.Count == 0 ? null : plan.FirstOrDefault(item => item.Day == requestedDay) ?? plan[^1];

    /// <summary>Formats an ISO date as a short human label, e.g. "Tue, Jul 22" (v4.2). Null-safe.</summary>
    private static string? FormatWhen(string? isoDate)
        => DateOnly.TryParse(isoDate?.Split('T')[0], out var d)
            ? d.ToString("ddd, MMM d", CultureInfo.InvariantCulture)
            : null;

    /// <summary>The feed is optional — the guest demo has none. Absent = no events, not a crash.</summary>
    private async Task<JsonObject> LoadFeedAsync(CancellationToken ct)
    {
        try
        {
            return await _store.LoadResolvedAsync("daily_events", ct);
        }
        catch (FileNotFoundException)
        {
            return new JsonObject { ["events"] = new JsonArray() };
        }
        catch (DirectoryNotFoundException)
        {
            return new JsonObject { ["events"] = new JsonArray() };
        }
    }
}
