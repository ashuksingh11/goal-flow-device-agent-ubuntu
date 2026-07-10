using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Steering;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Capabilities;

/// <summary>
/// CAPABILITY MODULE (shared): shopping list + grocery ordering.
/// SK plugin, name "ShoppingList". Backed by data/shopping_list.json.
/// The tier ladder lives here in miniature: reading is free, adding items is
/// LIGHT (rides the plan approval), placing the order SPENDS MONEY and is
/// FIRM — it never executes until an explicit approval decision, and the
/// SafetyFilter additionally blocks it when the estimate exceeds budget_cap.
/// </summary>
[Description("The family shopping list and grocery ordering.")]
public sealed class ShoppingListPlugin
{
    private readonly MockWorldStore _store;

    public ShoppingListPlugin(MockWorldStore store) => _store = store;

    [KernelFunction]
    [Description("Returns the current shopping list.")]
    public async Task<string> GetList(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("shopping_list", ct);
        return Json(doc);
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Adds items to the shopping list. Reversible; requires light (batched) approval.")]
    public async Task<string> Add(
        [Description("Item names to add, e.g. [\"lentils\",\"pasta\"].")] string[] items,
        [Description("Why these items are needed, e.g. \"for Tue & Thu dinners\".")] string? reason = null,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("shopping_list", ct);
        var list = doc["items"]!.AsArray();
        var existing = list.Select(n => n!["name"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => !existing.Contains(i)))
        {
            list.Add(new JsonObject
            {
                ["name"] = item,
                ["reason"] = reason,
                ["added_on"] = _store.Clock.Today.ToString("yyyy-MM-dd")
            });
        }

        await _store.SaveAsync("shopping_list", doc, ct);
        return Json(new JsonObject { ["status"] = "added", ["items"] = new JsonArray(items.Select(i => JsonValue.Create(i)).ToArray()) });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Removes items from the shopping list.")]
    public async Task<string> Remove(
        [Description("Item names to remove.")] string[] items,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("shopping_list", ct);
        var remove = items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = doc["items"]!.AsArray()
            .Where(n => !remove.Contains(n!["name"]!.GetValue<string>()))
            .Select(n => n!.DeepClone())
            .ToArray();
        doc["items"] = new JsonArray(kept);
        await _store.SaveAsync("shopping_list", doc, ct);
        return Json(new JsonObject { ["status"] = "removed", ["items"] = new JsonArray(items.Select(i => JsonValue.Create(i)).ToArray()) });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Firm)]
    [Description("Places the grocery order for the current list — SPENDS MONEY. Requires firm approval; blocked by safety if the estimate exceeds the budget cap.")]
    public async Task<string> PlaceOrder(
        [Description("Estimated order total in the household currency, e.g. 42.50.")] double estimatedTotal,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("shopping_list", ct);
        doc["ordered"] = true;
        doc["ordered_on"] = _store.Clock.Today.ToString("yyyy-MM-dd");
        doc["estimated_total"] = estimatedTotal;
        await _store.SaveAsync("shopping_list", doc, ct);
        return Json(new JsonObject { ["status"] = "ordered", ["estimated_total"] = estimatedTotal });
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
