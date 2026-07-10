using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Steering;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Capabilities;

/// <summary>
/// CAPABILITY MODULE (meal domain): the fridge's interior view.
/// SK plugin — every method is a [KernelFunction] the LLM calls directly via
/// auto function-calling. Registered as plugin name "Inventory".
/// Backed by data/inventory.json through <see cref="MockWorldStore"/>
/// (expiry stored as expires_in_days offsets, resolved against the clock).
/// </summary>
[Description("What food is currently in the fridge/pantry, including expiry.")]
public sealed class InventoryPlugin
{
    private readonly MockWorldStore _store;

    public InventoryPlugin(MockWorldStore store) => _store = store;

    [KernelFunction]
    [Description("Lists every item currently in the fridge/pantry with quantity, unit, category and expiry date.")]
    public async Task<string> ListItems(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("inventory", ct);
        return Json(doc["items"]);
    }

    [KernelFunction]
    [Description("Lists items that will expire within the given number of days from today — prime candidates for waste-rescue meals.")]
    public async Task<string> GetExpiringItems(
        [Description("Look-ahead horizon in days from today, e.g. 3.")] int withinDays,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("inventory", ct);
        var cutoff = _store.Clock.Today.AddDays(withinDays);
        var items = doc["items"]?.AsArray()
            .Where(n => n?["expires_in_days"] is not null && n["expires_in_days"]!.GetValueKind() != JsonValueKind.Null)
            .Where(n => _store.Clock.Today.AddDays(n!["expires_in_days"]!.GetValue<int>()) <= cutoff)
            .Select(n => n!.DeepClone())
            .ToArray() ?? [];
        return Json(new JsonArray(items));
    }

    [KernelFunction]
    [Description("Checks which of the given ingredients are available in sufficient quantity; returns available vs missing.")]
    public async Task<string> CheckAvailability(
        [Description("Ingredient names to check, e.g. [\"spinach\",\"rice\"].")] string[] ingredients,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("inventory", ct);
        var available = new JsonArray();
        var missing = new JsonArray();
        var inventory = doc["items"]?.AsArray()
            .Select(n => n!.AsObject())
            .ToDictionary(o => o["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            ?? [];

        foreach (var ingredient in ingredients)
        {
            if (inventory.TryGetValue(ingredient, out var item) && Quantity(item) > 0)
            {
                available.Add(item.DeepClone());
            }
            else
            {
                missing.Add(ingredient);
            }
        }

        return Json(new JsonObject { ["available"] = available, ["missing"] = missing });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Marks a quantity of an item as used/consumed (e.g. after a planned dinner). Reversible bookkeeping.")]
    public async Task<string> ConsumeItem(
        [Description("Inventory item name, e.g. \"spinach\".")] string name,
        [Description("Quantity consumed, in the item's unit.")] double quantity,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("inventory", ct);
        var item = doc["items"]?.AsArray()
            .Select(n => n!.AsObject())
            .FirstOrDefault(o => string.Equals(o["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Inventory item '{name}' was not found.");

        var remaining = Math.Max(0, Quantity(item) - quantity);
        item["quantity"] = remaining;
        await _store.SaveAsync("inventory", doc, ct);
        return Json(new JsonObject { ["status"] = "consumed", ["name"] = name, ["remaining"] = remaining });
    }

    private static double Quantity(JsonObject item)
        => item["quantity"]?.GetValue<double>() ?? 0;

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
