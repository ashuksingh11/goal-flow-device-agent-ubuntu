using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// Watches the household electricity bill (domain <c>energy_saving</c>).
///
/// <para>
/// The only goal in the pack with a MEASURABLE target: "cut the bill 15% without hurting
/// comfort". It has no plugin of its own by design — energy expresses itself through
/// tools that already exist: <c>Appliance.RunProgram</c> shifts a dishwasher to its eco
/// cycle in an off-peak window, <c>Reminders.Create</c> nudges a habit, <c>Notify</c>
/// reports the saving. The numbers live in <c>energy.json</c> (baseline, target, tariff
/// windows) and as per-appliance draw on <c>appliances.json</c>, which
/// <c>Appliance.ListAppliances</c> already returns to the planner.
/// </para>
///
/// <para>
/// Changes come from <c>energy.json</c>'s <c>pending_updates</c>, each with an
/// <c>activation_day_offset</c> resolved against the clock (generic-clock rule). A spike
/// or a peak-tariff window is a scheduling JUDGEMENT → it carries a
/// <see cref="WorldChange.Steer"/> for a scoped re-plan; standby waste is a simple
/// deterministic nudge.
/// </para>
/// </summary>
public sealed class EnergySavingObserver : IDomainObserver
{
    private readonly IProductApiAdapter _store;
    private readonly IClock _clock;

    public EnergySavingObserver(IProductApiAdapter store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public string Domain => "energy_saving";

    public string Hint => "cutting the electricity bill without hurting comfort — shifting appliances to off-peak, eco cycles, and standby waste";

    public async Task<JsonObject> CaptureAsync(CancellationToken ct = default)
    {
        var energy = await LoadEnergyAsync(ct);
        ResolvePendingUpdates(energy, _clock.Today);
        return new JsonObject
        {
            ["energy"] = energy,
            ["appliances"] = await _store.LoadResolvedAsync("appliances", ct)
        };
    }

    public IReadOnlyList<WorldChange> Observe(GoalRecord goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var updates = goal.WorldSnapshot["energy"]?["pending_updates"]?.AsArray() ?? [];

        return updates
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            .Where(u => u["activation_date"]?.GetValue<string>() is { } act && string.CompareOrdinal(today, act) >= 0)
            .Select(BuildChange)
            .ToArray();
    }

    private static WorldChange BuildChange(JsonObject update)
    {
        var kind = update["kind"]?.GetValue<string>() ?? "energy.update";
        var id = update["id"]?.GetValue<string>() ?? kind;
        var activation = update["activation_date"]?.GetValue<string>() ?? "";
        var appliance = update["appliance"]?.GetValue<string>() ?? "an appliance";
        var description = update["description"]?.GetValue<string>() ?? "The energy picture changed.";

        var change = new WorldChange
        {
            // STABLE key — reach-or-pass keeps returning this update, so no date in the key.
            Key = $"energy:{id}:{activation}",
            Kind = kind,
            Description = $"{description}. Activated on {activation}.",
            AffectedPlanItems = ["energy-schedule"],
            Context = update.DeepClone().AsObject(),
            Material = IsMaterial(kind)
        };

        return kind switch
        {
            "energy.standby_waste" => change with
            {
                RecommendedAction = $"Nudge the family to switch {appliance} off at the wall rather than leaving it on standby.",
                EffectModule = "Notify",
                EffectFunction = "SendNotification",
                EffectArgs = new JsonObject
                {
                    ["to"] = "family",
                    ["message"] = $"{appliance} is drawing standby power overnight — switching it off at the wall saves a little every night."
                },
                EffectAction = $"tell the family about {appliance} standby draw"
            },
            _ => change with
            {
                RecommendedAction = "Shift the heavy appliance runs out of the peak window and prefer eco cycles.",
                Steer = $"The energy picture changed: {description}. Re-plan the day's appliance schedule to protect the savings target — "
                      + "move heavy runs (dishwasher, laundry) into an off-peak window and prefer an eco program, "
                      + "without breaking quiet hours or the family's comfort."
            }
        };
    }

    /// <summary>
    /// Which energy moves are worth surfacing. A few watts of drift is noise; a spike
    /// against the baseline, a peak-tariff window, or a standby drain are not.
    /// </summary>
    private static bool IsMaterial(string kind) => kind switch
    {
        "energy.usage_spike" => true,
        "energy.peak_tariff" => true,
        "energy.standby_waste" => true,
        _ => false
    };

    private static void ResolvePendingUpdates(JsonObject energy, DateOnly capturedOn)
    {
        foreach (var update in (energy["pending_updates"]?.AsArray() ?? []).Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            if (update["activation_day_offset"] is not null)
            {
                update["activation_date"] = capturedOn.AddDays(update["activation_day_offset"]!.GetValue<int>()).ToString("yyyy-MM-dd");
            }
        }
    }

    /// <summary>energy.json is optional — absent means no energy events, not a crash.</summary>
    private async Task<JsonObject> LoadEnergyAsync(CancellationToken ct)
    {
        try
        {
            return await _store.LoadResolvedAsync("energy", ct);
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
