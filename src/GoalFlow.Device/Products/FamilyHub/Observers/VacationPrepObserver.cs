using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// Watches home readiness while the family is away (domain <c>vacation_prep</c>).
///
/// <para>
/// This is the use case v2 could not even START — its interpreter declined "get the
/// house ready, we're away next week" as out of scope. The M4 generic gate accepts
/// it because the device advertises the pieces that advance it (Security, Appliance,
/// Reminders, Calendar); this observer is what makes the DOMAIN real so the goal
/// routes here and keeps its adaptation watching instead of being labelled a slug no
/// observer answers to.
/// </para>
///
/// <para>
/// Changes come from <c>vacation.json</c>'s <c>pending_updates</c> — things that land
/// during the away window, each with an <c>activation_day_offset</c> resolved against
/// the clock. The seeded one is a parcel arriving to an empty house: material, because
/// a package on the porch signals nobody's home.
/// </para>
/// </summary>
public sealed class VacationPrepObserver : IDomainObserver
{
    private readonly IProductApiAdapter _store;
    private readonly IClock _clock;

    public VacationPrepObserver(IProductApiAdapter store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public string Domain => "vacation_prep";

    public string Hint => "getting the home ready while the family is away — locking up, arming security, saving energy, pausing deliveries";

    public async Task<JsonObject> CaptureAsync(CancellationToken ct = default)
    {
        var vacation = await LoadVacationAsync(ct);
        ResolvePendingUpdates(vacation, _clock.Today);
        return new JsonObject
        {
            ["vacation"] = vacation,
            ["security"] = await _store.LoadResolvedAsync("security", ct),
            ["appliances"] = await _store.LoadResolvedAsync("appliances", ct),
            ["calendar"] = await _store.LoadResolvedAsync("calendar", ct)
        };
    }

    public IReadOnlyList<WorldChange> Observe(GoalRecord goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var updates = goal.WorldSnapshot["vacation"]?["pending_updates"]?.AsArray() ?? [];

        return updates
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            // Reach-or-pass, like the guest and meal observers: advancing several
            // days at once must still surface the change.
            .Where(u => u["activation_date"]?.GetValue<string>() is { } act && string.CompareOrdinal(today, act) >= 0)
            .Select(u => BuildChange(u))
            .ToArray();
    }

    private static WorldChange BuildChange(JsonObject update)
    {
        var kind = update["kind"]?.GetValue<string>() ?? "vacation.update";
        var id = update["id"]?.GetValue<string>() ?? kind;
        var activation = update["activation_date"]?.GetValue<string>() ?? "";
        var description = update["description"]?.GetValue<string>() ?? "Something changed while the house was empty.";

        return new WorldChange
        {
            // STABLE key — keyed by update id + the activation day, never today,
            // or the reach-or-pass trigger would re-fire it daily.
            Key = $"vacation:{id}:{activation}",
            Kind = kind,
            Description = $"{description}. Activated on {activation}.",
            AffectedPlanItems = ["vacation-prep"],
            RecommendedAction = "Hold the delivery or ask a neighbour to collect it — a parcel on the porch signals an empty house.",
            EffectModule = "Notify",
            EffectFunction = "SendNotification",
            EffectArgs = new JsonObject
            {
                ["member"] = "Priya",
                ["message"] = "A parcel is due while you're away — I can ask a neighbour to hold it."
            },
            EffectAction = "notify about a delivery arriving to an empty house",
            Material = IsMaterial(kind)
        };
    }

    private static bool IsMaterial(string kind) => kind switch
    {
        "delivery.scheduled_while_away" => true,
        _ => false
    };

    /// <summary>Resolves each pending update's day offset against the clock.</summary>
    private static void ResolvePendingUpdates(JsonObject vacation, DateOnly capturedOn)
    {
        foreach (var update in (vacation["pending_updates"]?.AsArray() ?? []).Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            if (update["activation_day_offset"] is not null)
            {
                update["activation_date"] = capturedOn.AddDays(update["activation_day_offset"]!.GetValue<int>()).ToString("yyyy-MM-dd");
            }
        }
    }

    /// <summary>vacation.json is optional — absent means no away-window changes, not a crash.</summary>
    private async Task<JsonObject> LoadVacationAsync(CancellationToken ct)
    {
        try
        {
            return await _store.LoadResolvedAsync("vacation", ct);
        }
        catch (FileNotFoundException)
        {
            return new JsonObject { ["pending_updates"] = new JsonArray() };
        }
        catch (DirectoryNotFoundException)
        {
            return new JsonObject { ["pending_updates"] = new JsonArray() };
        }
    }
}
