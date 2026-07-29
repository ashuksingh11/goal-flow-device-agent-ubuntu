using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (shared): grocery/household budget awareness.
/// SK plugin, name "Budget". Backed by data/budget.json through
/// <see cref="IProductApiAdapter"/>.
///
/// READ-ONLY by design: the planner uses this to ESTIMATE so it can plan within
/// the cap. ENFORCEMENT of budget_cap is the SafetyFilter's job at
/// ShoppingList.PlaceOrder time (the numeric_cap rule) — "LLM plans, code checks".
///
/// <para>
/// v6 — THE CAP IS NOT DEVICE DATA. It comes from the goal's armed
/// <c>constraints.hard.budget_cap</c> via <see cref="IActivePolicy"/>, so the number
/// the planner plans against IS the number that will be enforced. data/budget.json
/// used to carry its own <c>cap: 120</c> beside the cloud's — two copies of one
/// policy, hand-synced, and the planner read the copy nothing enforced. It was also
/// wrong per goal: the cloud now sends $200 for a party and $1500 for a trip, and a
/// device-side 120 would have quietly contradicted both. What stays here is what is
/// genuinely device knowledge: what has been SPENT, and the price book.
/// </para>
/// </summary>
[Description("Grocery/household budget status and cost estimation.")]
public sealed class BudgetPlugin
{
    private readonly IProductApiAdapter _store;
    private readonly IActivePolicy _policy;

    public BudgetPlugin(IProductApiAdapter store, IActivePolicy policy)
    {
        _store = store;
        _policy = policy;
    }

    [KernelFunction]
    [Description("Returns the budget period, cap, amount spent so far, and remaining headroom.")]
    public async Task<string> GetBudgetStatus(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("budget", ct);
        var spent = doc["spent"]?.GetValue<double>() ?? 0;
        var status = new JsonObject
        {
            ["period"] = doc["period"]?.GetValue<string>() ?? "this week",
            ["currency"] = doc["currency"]?.GetValue<string>() ?? "USD",
            ["spent"] = spent
        };

        // No cap on this goal (or no goal scope) is a real answer, not a zero: an
        // energy-saving goal has no spend ceiling, and reporting "cap 0, remaining
        // -34.50" would tell the planner it is already over budget.
        var cap = _policy.ActiveHard()?["budget_cap"];
        if (cap is not null && cap.GetValueKind() != JsonValueKind.Null)
        {
            var ceiling = cap.GetValue<double>();
            status["cap"] = ceiling;
            status["remaining"] = Math.Round(ceiling - spent, 2);
        }

        return Json(status);
    }

    [KernelFunction]
    [Description("Estimates the total cost of a set of items using the household price book. Unknown items are priced at the default.")]
    public async Task<string> EstimateCost(
        [Description("Item names to price, e.g. [\"birthday cake\",\"balloons\"].")] string[] items,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("budget", ct);
        var prices = doc["prices"]?.AsObject();
        var fallback = doc["default_item_price"]?.GetValue<double>() ?? 4.0;

        var lines = new JsonArray();
        double total = 0;
        foreach (var item in items)
        {
            var price = LookUp(prices, item) ?? fallback;
            total += price;
            lines.Add(new JsonObject { ["item"] = item, ["price"] = price, ["estimated"] = LookUp(prices, item) is null });
        }

        return Json(new JsonObject
        {
            ["currency"] = doc["currency"]?.GetValue<string>() ?? "USD",
            ["items"] = lines,
            ["total"] = Math.Round(total, 2)
        });
    }

    /// <summary>Case-insensitive price lookup; null when the book doesn't know the item.</summary>
    private static double? LookUp(JsonObject? prices, string item)
    {
        if (prices is null)
        {
            return null;
        }

        foreach (var (key, value) in prices)
        {
            if (string.Equals(key, item, StringComparison.OrdinalIgnoreCase))
            {
                return value?.GetValue<double>();
            }
        }

        return null;
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
