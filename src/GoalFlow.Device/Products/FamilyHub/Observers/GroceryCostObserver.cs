using System.Text.Json;
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

    public string Hint => "keeping the kitchen stocked while minimising the GROCERY bill — stock levels, expiry, offers and prices. Household shopping only; it is not general personal finance";

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

    /// <summary>
    /// The pending-update feed PLUS the one change no feed announces: another goal
    /// spending the household envelope (v6-M3).
    /// </summary>
    public async Task<IReadOnlyList<WorldChange>> ObserveAsync(GoalRecord goal, CancellationToken ct = default)
    {
        var changes = Observe(goal).ToList();
        if (await EnvelopeSqueezeAsync(goal, ct) is { } squeeze)
        {
            changes.Add(squeeze);
        }

        return changes;
    }

    /// <summary>
    /// "Someone else spent the money." Material only when the envelope's remaining
    /// headroom has fallen BELOW this goal's own budget — until then another goal's
    /// spending is simply not this goal's problem, and waking a family for it would be
    /// the kind of noise that gets an agent switched off.
    ///
    /// <para>
    /// The comparison is against the goal's DISPATCHED cap, not its armed one: the
    /// armed cap is already narrowed by this same envelope, so comparing to it would
    /// be comparing a number to itself.
    /// </para>
    /// </summary>
    private async Task<WorldChange?> EnvelopeSqueezeAsync(GoalRecord goal, CancellationToken ct)
    {
        var hard = goal.Dispatch.Constraints.Hard;
        if (hard["budget_envelope"] is not JsonObject envelope
            || envelope["cap"]?.GetValueKind() != JsonValueKind.Number
            || hard["budget_cap"]?.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        var planned = goal.WorldSnapshot["budget"]?["spent"]?.GetValue<double>() ?? 0;
        var spent = (await _store.LoadResolvedAsync("budget", ct))["spent"]?.GetValue<double>() ?? 0;
        if (spent <= planned)
        {
            return null;
        }

        var cap = envelope["cap"]!.GetValue<double>();
        var goalCap = hard["budget_cap"]!.GetValue<double>();
        var remaining = Math.Round(Math.Max(0, cap - spent), 2);
        if (remaining >= goalCap)
        {
            return null;
        }

        return new WorldChange
        {
            // STABLE per spend level: the squeeze keeps being true every day after it
            // happens, so keying on today would re-raise it every tick.
            Key = $"budget:envelope:{spent:0.00}",
            Kind = "budget.envelope_squeezed",
            Description =
                $"Another goal has spent from the household budget: ${remaining:0.00} of the ${cap:0.00} "
                + $"envelope is left, less than this goal's ${goalCap:0.00}.",
            AffectedPlanItems = ["grocery-basket"],
            Context = new JsonObject
            {
                ["envelope_cap"] = cap,
                ["spent"] = Math.Round(spent, 2),
                ["remaining"] = remaining,
                ["goal_cap"] = goalCap
            },
            Material = true,
            RecommendedAction = $"Re-plan the basket to fit the ${remaining:0.00} left in the household budget.",
            Steer =
                $"The household budget is shared, and another goal has spent from it: only ${remaining:0.00} is left "
                + $"this period, below this goal's ${goalCap:0.00}. Re-plan the shopping to fit ${remaining:0.00} — defer "
                + "what is not urgent, substitute cheaper equivalents, and keep the hard dietary constraints intact."
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
