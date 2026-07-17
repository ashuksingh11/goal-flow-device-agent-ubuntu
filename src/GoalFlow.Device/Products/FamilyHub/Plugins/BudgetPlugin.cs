using System.ComponentModel;
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
/// The cap returned here is informational; the hard cap rides constraints.hard.
/// </summary>
[Description("Grocery/household budget status and cost estimation.")]
public sealed class BudgetPlugin
{
    private readonly IProductApiAdapter _store;

    public BudgetPlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Returns the budget period, cap, amount spent so far, and remaining headroom.")]
    public async Task<string> GetBudgetStatus(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("budget", ct);
        var cap = doc["cap"]?.GetValue<double>() ?? 0;
        var spent = doc["spent"]?.GetValue<double>() ?? 0;
        return Json(new JsonObject
        {
            ["period"] = doc["period"]?.GetValue<string>() ?? "this week",
            ["currency"] = doc["currency"]?.GetValue<string>() ?? "USD",
            ["cap"] = cap,
            ["spent"] = spent,
            ["remaining"] = Math.Round(cap - spent, 2)
        });
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
