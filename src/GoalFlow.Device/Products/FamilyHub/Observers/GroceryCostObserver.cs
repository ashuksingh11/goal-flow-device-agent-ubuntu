using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// Watches the running grocery bill (domain <c>grocery_cost</c>).
///
/// <para>
/// Distinct from <c>meal_plan</c>: a meal week plans WHAT to eat, this watches WHAT IT
/// COSTS to keep the kitchen stocked — stock falling below its restock threshold, an
/// offer worth acting on, a staple whose price moved. The teeth are already in place:
/// Budget.EstimateCost prices a basket against the household price book, and
/// ShoppingList.PlaceOrder is FIRM-tier and blocked by the numeric_cap safety rule when
/// a basket exceeds the dispatch's budget_cap — so "cheaper" is enforced, not suggested.
/// </para>
///
/// <para>
/// Changes come from <c>grocery.json</c>'s <c>pending_updates</c>, each with an
/// <c>activation_day_offset</c> resolved against the clock (generic-clock rule). A price
/// or offer move carries a <see cref="WorldChange.Steer"/> so the adaptation is a scoped
/// re-plan (swap, defer, buy-ahead); a stock-out is deterministic — it just needs adding.
/// </para>
/// </summary>
public sealed class GroceryCostObserver : IDomainObserver
{
    private readonly IProductApiAdapter _store;
    private readonly IClock _clock;

    public GroceryCostObserver(IProductApiAdapter store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public string Domain => "grocery_cost";

    public string Hint => "keeping the kitchen stocked while minimising the grocery bill — stock levels, expiry, offers and prices against a budget";

    public async Task<JsonObject> CaptureAsync(CancellationToken ct = default)
    {
        var grocery = await LoadGroceryAsync(ct);
        ResolvePendingUpdates(grocery, _clock.Today);
        return new JsonObject
        {
            ["grocery"] = grocery,
            ["inventory"] = await _store.LoadResolvedAsync("inventory", ct),
            ["budget"] = await _store.LoadResolvedAsync("budget", ct)
        };
    }

    public IReadOnlyList<WorldChange> Observe(GoalRecord goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var updates = goal.WorldSnapshot["grocery"]?["pending_updates"]?.AsArray() ?? [];

        return updates
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            .Where(u => u["activation_date"]?.GetValue<string>() is { } act && string.CompareOrdinal(today, act) >= 0)
            .Select(BuildChange)
            .ToArray();
    }

    private static WorldChange BuildChange(JsonObject update)
    {
        var kind = update["kind"]?.GetValue<string>() ?? "grocery.update";
        var id = update["id"]?.GetValue<string>() ?? kind;
        var activation = update["activation_date"]?.GetValue<string>() ?? "";
        var item = update["item"]?.GetValue<string>() ?? "an item";
        var description = update["description"]?.GetValue<string>() ?? "The grocery picture changed.";

        var change = new WorldChange
        {
            // STABLE key — the feed keeps returning this update every day after its
            // activation date, so the key must not embed today.
            Key = $"grocery:{id}:{activation}",
            Kind = kind,
            Description = $"{description}. Activated on {activation}.",
            AffectedPlanItems = ["grocery-basket"],
            Context = update.DeepClone().AsObject(),
            Material = IsMaterial(kind)
        };

        // A price/offer move is a JUDGEMENT call (buy ahead? substitute? wait?) → hand it
        // to a scoped re-plan. A stock-out is not — the item simply has to go on the list.
        return kind switch
        {
            "grocery.stock_low" => change with
            {
                RecommendedAction = $"Add {item} to the shopping list before it runs out.",
                EffectModule = "ShoppingList",
                EffectFunction = "Add",
                EffectArgs = new JsonObject
                {
                    ["items"] = new JsonArray(item),
                    ["reason"] = "grocery adaptation: stock below threshold"
                },
                EffectAction = $"add {item} to the shopping list"
            },
            _ => change with
            {
                RecommendedAction = $"Re-check the basket against the budget in light of the {item} price change.",
                Steer = $"The price picture changed: {description}. Re-plan the shopping so the basket still fits the budget cap — "
                      + "buy ahead while it is cheap, substitute a cheaper equivalent, or defer a non-urgent item. "
                      + "Keep the household's dietary constraints intact."
            }
        };
    }

    /// <summary>
    /// Which grocery moves are worth waking the family for. Curated like the meal feed:
    /// a price wobble on something nobody buys is noise, so only these kinds surface.
    /// </summary>
    private static bool IsMaterial(string kind) => kind switch
    {
        "grocery.stock_low" => true,
        "grocery.offer_available" => true,
        "grocery.price_rose" => true,
        _ => false
    };

    private static void ResolvePendingUpdates(JsonObject grocery, DateOnly capturedOn)
    {
        foreach (var update in (grocery["pending_updates"]?.AsArray() ?? []).Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            if (update["activation_day_offset"] is not null)
            {
                update["activation_date"] = capturedOn.AddDays(update["activation_day_offset"]!.GetValue<int>()).ToString("yyyy-MM-dd");
            }
        }
    }

    /// <summary>grocery.json is optional — absent means no price moves, not a crash.</summary>
    private async Task<JsonObject> LoadGroceryAsync(CancellationToken ct)
    {
        try
        {
            return await _store.LoadResolvedAsync("grocery", ct);
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
