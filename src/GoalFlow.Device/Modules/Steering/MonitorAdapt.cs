using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Capabilities;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace GoalFlow.Device.Modules.Steering;

/// <summary>
/// HARNESS MODULE: Monitor &amp; Adapt.
/// After a plan is approved, the world keeps moving (day advances, an item
/// expires early, a guest RSVPs an allergy). This module compares fresh world
/// state against the plan's assumptions, applies the MATERIALITY POLICY
/// (deterministic code — not the LLM — decides whether a change matters), and
/// when material, produces a scoped adaptation <see cref="Proposal"/> that
/// re-plans ONLY the affected slice.
/// </summary>
public sealed class MonitorAdapt
{
    private readonly IClock _clock;
    private readonly MockWorldStore _store;
    private readonly MaterialityPolicy _policy;
    private readonly ApprovalCoordinator _approvals;
    private readonly ILogger<MonitorAdapt> _logger;

    public MonitorAdapt(IClock clock, MockWorldStore store, MaterialityPolicy policy, ApprovalCoordinator approvals, ILogger<MonitorAdapt> logger)
    {
        _clock = clock;
        _store = store;
        _policy = policy;
        _approvals = approvals;
        _logger = logger;
    }

    /// <summary>
    /// Re-reads world state (via the capability plugins) as of the clock's
    /// current date and diffs it against the approved plan's assumptions.
    /// Returns detected changes, each pre-classified by the materiality policy.
    /// </summary>
    public Task<IReadOnlyList<WorldChange>> ObserveAsync(string goalId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorldChange>>([]);

    public Task<IReadOnlyList<WorldChange>> ObserveAsync(ActiveGoalContext goal, CancellationToken ct = default)
    {
        var changes = goal.Dispatch.Domain switch
        {
            "meal_plan" => ObserveMealChanges(goal),
            "guest_dinner" => ObserveGuestChanges(goal),
            _ => []
        };

        return Task.FromResult<IReadOnlyList<WorldChange>>(changes
            .Select(c => c with { Material = _policy.IsMaterial(c) })
            .ToArray());
    }

    /// <summary>
    /// For a material change: build the adaptation proposal (tier "adapt"-level
    /// consent = requires approval) — a scoped re-plan, e.g. "swap Thursday's
    /// dinner; spinach expired". Non-material changes yield null (logged only).
    /// </summary>
    public Task<Proposal?> ProposeAdaptationAsync(string goalId, WorldChange change, CancellationToken ct = default)
    {
        if (!change.Material)
        {
            return Task.FromResult<Proposal?>(null);
        }

        var proposalId = $"a{_approvals.All().Count(p => p.ProposalId.StartsWith('a')) + 1}";
        var effect = BuildEffect(proposalId, change);
        _approvals.Register(effect);

        var proposal = new Proposal
        {
            GoalId = goalId,
            TaskStatus = TaskStatuses.Adapting,
            Payload = new AdaptationPayload
            {
                ProposalId = proposalId,
                Action = effect.Action,
                Detail = effect.Reason,
                Trigger = change.Description,
                Tier = ApprovalTiers.Adapt,
                RequiresApproval = true
            }
        };

        _logger.LogInformation("adaptation_proposed {ProposalId} kind={Kind} trigger={Trigger}", proposalId, change.Kind, change.Description);
        return Task.FromResult<Proposal?>(proposal);
    }

    public async Task<JsonObject> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = new JsonObject
        {
            ["captured_on"] = _clock.Today.ToString("yyyy-MM-dd"),
            ["calendar"] = await _store.LoadResolvedAsync("calendar", ct),
            ["guests"] = await _store.LoadResolvedAsync("guests", ct),
            ["recipes"] = await _store.LoadResolvedAsync("recipes", ct),
            ["daily_events"] = await LoadDailyEventsAsync(ct)
        };

        ResolveGuestPendingUpdates(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Meal-week changes come from the DAILY WORLD-CHANGE FEED (data/daily_events.json):
    /// one curated, believable real-world change per day (fridge restock, an item
    /// running out, a calendar clash, an extra guest, an appliance going down). Each
    /// fires when the clock REACHES OR PASSES its day (deduped once by id). The
    /// materiality is curated — every feed entry matters — and GoalAgent runs a
    /// scoped LLM re-plan against the entry's `context`/`steer` to patch the plan.
    /// </summary>
    private IReadOnlyList<WorldChange> ObserveMealChanges(ActiveGoalContext goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var feed = goal.WorldSnapshot["daily_events"]?["events"]?.AsArray() ?? [];
        var changes = new List<WorldChange>();

        foreach (var ev in feed.Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            var evDate = ev["date"]?.GetValue<string>();
            // Reach-or-pass: fire on the first tick on/after the event's day.
            if (evDate is null || string.CompareOrdinal(today, evDate) < 0)
            {
                continue;
            }

            changes.Add(BuildDailyEventChange(goal, ev));
        }

        return changes;
    }

    public WorldChange BuildDailyEventChange(ActiveGoalContext goal, JsonObject ev)
    {
        var evDate = ev["date"]?.GetValue<string>();
        var id = ev["id"]?.GetValue<string>() ?? evDate ?? "unknown";
        var affected = goal.Plan
            .Where(item => PlanItemDate(item) == evDate)
            .Select(item => item.Id)
            .DefaultIfEmpty(evDate is null ? "dinner" : $"dinner-{evDate}")
            .ToArray();

        var change = new WorldChange
        {
            // STABLE key — the event fires exactly once even though the feed
            // keeps returning it every day after its date (dedup in GoalAgent).
            Key = $"daily:{id}",
            Kind = ev["kind"]?.GetValue<string>() ?? "world.change",
            Description = ev["summary"]?.GetValue<string>() ?? "A change occurred in the home.",
            AffectedPlanItems = affected,
            RecommendedAction = ev["steer"]?.GetValue<string>(),
            Steer = ev["steer"]?.GetValue<string>(),
            Context = ev["context"]?.AsObject()?.DeepClone().AsObject()
        };

        return change with { Material = _policy.IsMaterial(change) };
    }

    public IReadOnlyList<DemoEvent> GetDemoEventsCatalog(JsonObject snapshot)
        => (snapshot["daily_events"]?["events"]?.AsArray() ?? [])
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            .Select(ev => new DemoEvent
            {
                Id = ev["id"]?.GetValue<string>() ?? "",
                Label = ev["label"]?.GetValue<string>() ?? "",
                Title = ev["title"]?.GetValue<string>() ?? "",
                Kind = ev["kind"]?.GetValue<string>() ?? "world.change",
                Order = ev["order"]?.GetValue<int>() ?? int.MaxValue
            })
            .Where(ev => ev.Id.Length > 0)
            .OrderBy(ev => ev.Order)
            .ToArray();

    private async Task<JsonObject> LoadDailyEventsAsync(CancellationToken ct)
    {
        // The feed is optional (guest-dinner demo has no meal feed) — an absent
        // file yields an empty event list rather than throwing.
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

    private IReadOnlyList<WorldChange> ObserveGuestChanges(ActiveGoalContext goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var updates = goal.WorldSnapshot["guests"]?["pending_updates"]?.AsArray() ?? [];
        var eventDate = goal.WorldSnapshot["guests"]?["events"]?.AsArray()
            .Select(n => n?.AsObject())
            .FirstOrDefault()?["date"]?.GetValue<string>();
        var affected = goal.Plan
            .Where(item => PlanItemDate(item) == eventDate || item.Tags.Any(t => t.Contains("menu", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Id)
            .DefaultIfEmpty("guest-menu")
            .ToArray();

        return updates
            .Select(n => n?.AsObject())
            // Fire when the clock REACHES OR PASSES the activation day — not only on
            // the exact day — so advancing several days at once (or a bit past it)
            // still surfaces the RSVP change. Deduped once by the stable Key below.
            .Where(update => update?["activation_date"]?.GetValue<string>() is { } act
                && string.CompareOrdinal(today, act) >= 0)
            .Select(update =>
            {
                var guestName = update!["guest_name"]?.GetValue<string>() ?? "A guest";
                var kind = update["kind"]?.GetValue<string>() ?? "guest.update";
                var trigger = update["description"]?.GetValue<string>() ?? $"{guestName} guest details changed.";
                var activation = update["activation_date"]?.GetValue<string>() ?? today;
                var isLate = kind.Contains("late", StringComparison.OrdinalIgnoreCase) || trigger.Contains("late", StringComparison.OrdinalIgnoreCase);
                return new WorldChange
                {
                    // STABLE key — deduped to exactly ONCE. It must NOT embed `today`:
                    // with the reach-or-pass (>=) trigger the change is observed every
                    // day after activation, so a today-stamped key would re-propose the
                    // same RSVP every single day (the meal key is stable for the same
                    // reason). Keyed by the update id + the activation day it fired on.
                    Key = $"guest:{update["id"]?.GetValue<string>() ?? guestName}:{activation}",
                    Kind = kind,
                    Description = $"{trigger} Activated on {activation}.",
                    AffectedPlanItems = affected,
                    RecommendedAction = isLate
                        ? "Move prep earlier so the compressed serve window still works."
                        : "Swap the affected dish to a nut-free option.",
                    EffectModule = "ShoppingList",
                    EffectFunction = "Add",
                    EffectArgs = new JsonObject
                    {
                        ["items"] = new JsonArray("fruit platter", "seed-free crackers"),
                        ["reason"] = "guest adaptation: nut-free backup dish"
                    },
                    EffectAction = "add nut-free backup dish ingredients"
                };
            })
            .ToArray();
    }

    private static ProposalItem BuildEffect(string proposalId, WorldChange change)
        => new()
        {
            ProposalId = proposalId,
            Action = change.EffectAction ?? change.RecommendedAction ?? "adapt plan",
            Module = change.EffectModule ?? "Reminders",
            Function = change.EffectFunction ?? "Create",
            Args = change.EffectArgs?.DeepClone().AsObject(),
            Tier = ApprovalTiers.Adapt,
            Reason = $"{change.RecommendedAction} Trigger: {change.Description}",
            RequiresApproval = true
        };

    private static bool PrepWindowOverlaps(PlanItem item, JsonObject calendarEvent)
    {
        var prepStart = TimeOnly.Parse("17:30");
        var prepEnd = TimeOnly.Parse("18:30");
        var eventStart = TimeOnly.Parse(calendarEvent["start"]!.GetValue<string>());
        var eventEnd = TimeOnly.Parse(calendarEvent["end"]!.GetValue<string>());
        return prepStart < eventEnd && prepEnd > eventStart;
    }

    private static string? PlanItemDate(PlanItem item)
    {
        if (string.IsNullOrWhiteSpace(item.When))
        {
            return null;
        }

        return DateTimeOffset.TryParse(item.When, out var dto)
            ? DateOnly.FromDateTime(dto.DateTime).ToString("yyyy-MM-dd")
            : DateOnly.TryParse(item.When, out var date) ? date.ToString("yyyy-MM-dd") : null;
    }

    private static void ResolveGuestPendingUpdates(JsonObject snapshot)
    {
        var guests = snapshot["guests"]?.AsObject();
        var capturedOn = DateOnly.Parse(snapshot["captured_on"]!.GetValue<string>());
        var updates = guests?["pending_updates"]?.AsArray();
        if (updates is null)
        {
            return;
        }

        foreach (var update in updates.Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            if (update["activation_day_offset"] is not null)
            {
                update["activation_date"] = capturedOn.AddDays(update["activation_day_offset"]!.GetValue<int>()).ToString("yyyy-MM-dd");
            }
        }
    }
}

/// <summary>
/// Deterministic materiality rules — what is worth waking the user for.
/// E.g. an ingredient a PLANNED dish depends on expiring is material; a pantry
/// item no dish uses expiring is not.
/// </summary>
public sealed class MaterialityPolicy
{
    /// <summary>True when the change invalidates part of the approved plan.</summary>
    public bool IsMaterial(WorldChange change)
        => change.Kind switch
        {
            // Guest-domain triggers stay gated on actually touching a plan item.
            "guest.rsvp_allergy_added" => change.AffectedPlanItems.Count > 0,
            "guest.arrival_late" => change.AffectedPlanItems.Count > 0,
            // Daily-feed changes are a CURATED set — every entry is a real-world
            // change worth re-planning for (the feed is the materiality decision).
            "calendar.event_overlap" => true,
            "inventory.restocked" => true,
            "inventory.shortage" => true,
            "guest.headcount_added" => true,
            "appliance.unavailable" => true,
            "meal.lighter_requested" => true,
            _ => false
        };
}

/// <summary>One observed world-state delta.</summary>
public sealed record WorldChange
{
    public required string Key { get; init; }

    /// <summary>E.g. "inventory.expired", "calendar.event_added", "guest.rsvp_changed".</summary>
    public required string Kind { get; init; }

    /// <summary>Human-readable description — becomes the proposal's trigger.</summary>
    public required string Description { get; init; }

    /// <summary>Plan-item ids this change touches (the re-plan slice).</summary>
    public IReadOnlyList<string> AffectedPlanItems { get; init; } = [];

    /// <summary>Filled by the materiality policy.</summary>
    public bool Material { get; init; }

    public string? RecommendedAction { get; init; }

    public string? EffectModule { get; init; }

    public string? EffectFunction { get; init; }

    public JsonObject? EffectArgs { get; init; }

    public string? EffectAction { get; init; }

    /// <summary>Structured detail for the scoped LLM re-plan (e.g. the added items,
    /// the unavailable ingredients, the appliance). Null for non-feed changes.</summary>
    public JsonObject? Context { get; init; }

    /// <summary>A one-line nudge telling the planner HOW to adapt to this change —
    /// steers the LLM output without hardcoding the new plan.</summary>
    public string? Steer { get; init; }
}

public sealed record ActiveGoalContext
{
    public required Dispatch Dispatch { get; init; }

    /// <summary>The live plan — SETTABLE so an approved daily adaptation can patch
    /// it in place without recreating the context (which would reset the dedup set).</summary>
    public required IReadOnlyList<PlanItem> Plan { get; set; }

    public required JsonObject WorldSnapshot { get; init; }

    public HashSet<string> EmittedMaterialChanges { get; } = new(StringComparer.Ordinal);
}
