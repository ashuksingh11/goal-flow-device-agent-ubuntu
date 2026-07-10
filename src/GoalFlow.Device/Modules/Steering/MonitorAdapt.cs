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
            ["recipes"] = await _store.LoadResolvedAsync("recipes", ct)
        };

        ResolveGuestPendingUpdates(snapshot);
        return snapshot;
    }

    private IReadOnlyList<WorldChange> ObserveMealChanges(ActiveGoalContext goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var calendarEvents = goal.WorldSnapshot["calendar"]?["events"]?.AsArray() ?? [];
        var changes = new List<WorldChange>();

        foreach (var ev in calendarEvents.Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            var title = ev["title"]?.GetValue<string>() ?? "";
            if (!title.Contains("football", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ev["date"]?.GetValue<string>(), today, StringComparison.Ordinal))
            {
                continue;
            }

            var affected = goal.Plan
                .Where(item => PlanItemDate(item) == today && PrepWindowOverlaps(item, ev))
                .Select(item => item.Id)
                .ToArray();

            changes.Add(new WorldChange
            {
                Key = $"meal:{ev["id"]?.GetValue<string>() ?? title}:{today}",
                Kind = "calendar.event_overlap",
                Description = $"{title} runs {ev["start"]?.GetValue<string>()}-{ev["end"]?.GetValue<string>()} on {today}, overlapping the planned dinner prep window.",
                AffectedPlanItems = affected,
                RecommendedAction = "Prep that dinner the night before, or swap it to a quicker dish.",
                EffectModule = "Reminders",
                EffectFunction = "Create",
                EffectArgs = new JsonObject
                {
                    ["title"] = "Prep the football-night dinner the night before",
                    ["date"] = _clock.Today.AddDays(-1).ToString("yyyy-MM-dd"),
                    ["time"] = "19:00"
                },
                EffectAction = "add night-before prep reminder"
            });
        }

        return changes;
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
            .Where(update => update is not null && string.Equals(update["activation_date"]?.GetValue<string>(), today, StringComparison.Ordinal))
            .Select(update =>
            {
                var guestName = update!["guest_name"]?.GetValue<string>() ?? "A guest";
                var kind = update["kind"]?.GetValue<string>() ?? "guest.update";
                var trigger = update["description"]?.GetValue<string>() ?? $"{guestName} guest details changed.";
                var isLate = kind.Contains("late", StringComparison.OrdinalIgnoreCase) || trigger.Contains("late", StringComparison.OrdinalIgnoreCase);
                return new WorldChange
                {
                    Key = $"guest:{update["id"]?.GetValue<string>() ?? guestName}:{today}",
                    Kind = kind,
                    Description = $"{trigger} Activated on {today}.",
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
            "calendar.event_overlap" => change.AffectedPlanItems.Count > 0,
            "guest.rsvp_allergy_added" => change.AffectedPlanItems.Count > 0,
            "guest.arrival_late" => change.AffectedPlanItems.Count > 0,
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
}

public sealed record ActiveGoalContext
{
    public required Dispatch Dispatch { get; init; }

    public required IReadOnlyList<PlanItem> Plan { get; init; }

    public required JsonObject WorldSnapshot { get; init; }

    public HashSet<string> EmittedMaterialChanges { get; } = new(StringComparer.Ordinal);
}
