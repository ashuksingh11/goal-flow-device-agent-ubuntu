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
/// MATERIALITY: the feed IS the materiality decision, curated so the demo shows quiet
/// days and one smart adaptation rather than a stream of noise. Through v6 every entry
/// was material and nothing else entered the feed. v7 adds the other case —
/// <c>workout.activity_logged</c> is worth TELLING the family about and not worth
/// re-planning for on its own — so the feed now carries both, and
/// <see cref="IsMaterial"/> is where the difference is decided rather than at the point
/// of writing an entry.
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

    public string Hint => "planning the week's dinners at home — using up what is in the fridge, household dietary rules, the family's activity";

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

        // v7 — THE DAY IS ALREADY EMPTY. Another goal the family approved marked this day
        // "away, no meal planned", and a world change cannot un-empty it: the paneer really
        // did spoil, but there is no dinner on Sunday to move it to. So the change is still
        // TOLD (it stays in "what happened today") and simply stops being something to
        // re-plan for. Demoting rather than dropping it is the point — a family that is
        // away still wants to know their fridge lost something.
        var awayDay = targetItem?.Status == PlanItemStatuses.Skipped;
        return new WorldChange
        {
            // STABLE key — the feed keeps returning this event every day after its
            // date, so the key must not embed today or it would re-fire daily.
            Key = $"daily:{id}",
            Kind = kind,
            Description = awayDay
                ? $"{whenLabel} - {summary} No dinner was planned — you're away."
                : $"{whenLabel} - {summary}",
            AffectedPlanItems = affected,
            TargetDay = targetDay,
            TargetItemId = targetItem?.Id,
            TargetTitle = targetItem?.Title,
            // TWO STRINGS, TWO AUDIENCES — and they were the same string until v9.
            //
            // `Steer` is written TO THE MODEL: "…Say both reasons in the why. Drop items now
            // in stock from the shopping list." `RecommendedAction` is written to a PERSON —
            // it is the headline on the adaptation card and the line on their board. Setting
            // both from `steer` put our prompt on the fridge door, directives and all, which
            // is how the demo ended up asking a family to "say both reasons in the why".
            //
            // Every other observer in this folder already authors a real sentence here; this
            // one is the only one reading it out of the feed, so the feed grew an `action`
            // beside each `steer`. The fallback is the SUMMARY, never the steer: a feed entry
            // that forgets its action should read as the event that happened, not as an
            // instruction to a language model.
            RecommendedAction = ev["action"]?.GetValue<string>() ?? summary,
            Steer = ev["steer"]?.GetValue<string>(),
            Context = context,
            Material = IsMaterial(kind) && !awayDay
        };
    }

    /// <summary>
    /// Which meal-week changes are worth RE-PLANNING for. Anything unrecognised is
    /// not, so a stray entry stays quiet rather than nagging.
    ///
    /// <para>
    /// A non-material kind is not ignored: it is still surfaced in the day summary
    /// under Advance day, so the family is told it happened. It simply does not open
    /// an approval. <c>workout.activity_logged</c> is the case that motivated the
    /// distinction — knowing yesterday was a hard training day is worth saying, and
    /// asking someone to approve a plan change because they went for a run is not.
    /// The day it matters, it matters as a REASON inside another change's steer, which
    /// is exactly how the fish delivery uses it.
    /// </para>
    /// </summary>
    private static bool IsMaterial(string kind) => kind switch
    {
        "calendar.event_overlap" => true,
        "inventory.restocked" => true,
        "inventory.shortage" => true,
        "guest.headcount_added" => true,
        "appliance.unavailable" => true,
        "meal.lighter_requested" => true,
        // Explicitly listed rather than left to the default: this one is a decision.
        "workout.activity_logged" => false,
        _ => false
    };

    /// <summary>
    /// The plan row a feed entry edits — its own day when the plan still has one, and
    /// otherwise the last row, because a change with nowhere to land is better aimed at
    /// the end of the week than dropped.
    ///
    /// <para>
    /// The fallback deliberately prefers the last row that is NOT skipped: an away day is
    /// not a place to put a dinner, and landing there would demote a change that had a
    /// perfectly good home two rows earlier.
    /// </para>
    /// </summary>
    private static PlanItem? FindTargetPlanItem(IReadOnlyList<PlanItem> plan, int requestedDay)
        => plan.Count == 0
            ? null
            : plan.FirstOrDefault(item => item.Day == requestedDay)
              ?? plan.LastOrDefault(item => item.Status != PlanItemStatuses.Skipped)
              ?? plan[^1];

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
