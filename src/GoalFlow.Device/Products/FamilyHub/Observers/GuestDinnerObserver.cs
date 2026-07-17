using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// Watches a hosted dinner (domain <c>guest_dinner</c>).
///
/// <para>
/// Changes come from <c>guests.pending_updates</c> — RSVPs that land after the
/// plan was made: a guest declares a nut allergy, someone will arrive late. Each
/// carries an <c>activation_day_offset</c> resolved against the clock, so the
/// change surfaces when the simulated week reaches it.
/// </para>
/// </summary>
public sealed class GuestDinnerObserver : IDomainObserver
{
    private readonly IProductApiAdapter _store;
    private readonly IClock _clock;

    public GuestDinnerObserver(IProductApiAdapter store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public string Domain => "guest_dinner";

    public async Task<JsonObject> CaptureAsync(CancellationToken ct = default)
    {
        var guests = await _store.LoadResolvedAsync("guests", ct);
        ResolvePendingUpdates(guests, _clock.Today);
        return new JsonObject
        {
            ["guests"] = guests,
            ["calendar"] = await _store.LoadResolvedAsync("calendar", ct),
            ["recipes"] = await _store.LoadResolvedAsync("recipes", ct)
        };
    }

    public IReadOnlyList<WorldChange> Observe(GoalRecord goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var updates = goal.WorldSnapshot["guests"]?["pending_updates"]?.AsArray() ?? [];
        var eventDate = goal.WorldSnapshot["guests"]?["events"]?.AsArray()
            .Select(n => n?.AsObject())
            .FirstOrDefault()?["date"]?.GetValue<string>();

        // The menu items this event touches — what a late RSVP would invalidate.
        var affected = goal.Plan
            .Where(item => PlanItemDate(item) == eventDate || item.Tags.Any(t => t.Contains("menu", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Id)
            .DefaultIfEmpty("guest-menu")
            .ToArray();

        return updates
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            // Reach-or-pass, not exact-day: advancing several days at once must
            // still surface the RSVP.
            .Where(u => u["activation_date"]?.GetValue<string>() is { } act && string.CompareOrdinal(today, act) >= 0)
            .Select(u => BuildChange(u, affected, today))
            .ToArray();
    }

    private static WorldChange BuildChange(JsonObject update, IReadOnlyList<string> affected, string today)
    {
        var guestName = update["guest_name"]?.GetValue<string>() ?? "A guest";
        var kind = update["kind"]?.GetValue<string>() ?? "guest.update";
        var trigger = update["description"]?.GetValue<string>() ?? $"{guestName} guest details changed.";
        var activation = update["activation_date"]?.GetValue<string>() ?? today;
        var isLate = kind.Contains("late", StringComparison.OrdinalIgnoreCase)
                     || trigger.Contains("late", StringComparison.OrdinalIgnoreCase);

        return new WorldChange
        {
            // STABLE key — keyed by update id + the activation day it fired on,
            // never by today: with the reach-or-pass trigger, a today-stamped key
            // would re-propose the same RSVP every single day.
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
            EffectAction = "add nut-free backup dish ingredients",
            // Guest changes are only material if they actually touch the plan —
            // unlike the meal feed, these are not a curated set.
            Material = IsMaterial(kind, affected)
        };
    }

    private static bool IsMaterial(string kind, IReadOnlyList<string> affected) => kind switch
    {
        "guest.rsvp_allergy_added" => affected.Count > 0,
        "guest.arrival_late" => affected.Count > 0,
        _ => false
    };

    /// <summary>Resolves each pending update's day offset against the clock (never store absolute dates).</summary>
    private static void ResolvePendingUpdates(JsonObject guests, DateOnly capturedOn)
    {
        foreach (var update in (guests["pending_updates"]?.AsArray() ?? []).Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            if (update["activation_day_offset"] is not null)
            {
                update["activation_date"] = capturedOn.AddDays(update["activation_day_offset"]!.GetValue<int>()).ToString("yyyy-MM-dd");
            }
        }
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
}
