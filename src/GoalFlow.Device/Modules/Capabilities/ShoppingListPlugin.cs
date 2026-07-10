using System.ComponentModel;
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
    public Task<string> GetList(CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): read shopping_list.json");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Adds items to the shopping list. Reversible; requires light (batched) approval.")]
    public Task<string> Add(
        [Description("Item names to add, e.g. [\"lentils\",\"pasta\"].")] string[] items,
        [Description("Why these items are needed, e.g. \"for Tue & Thu dinners\".")] string? reason = null,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): append items, persist via MockWorldStore.SaveAsync");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Removes items from the shopping list.")]
    public Task<string> Remove(
        [Description("Item names to remove.")] string[] items,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): remove items, persist");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Firm)]
    [Description("Places the grocery order for the current list — SPENDS MONEY. Requires firm approval; blocked by safety if the estimate exceeds the budget cap.")]
    public Task<string> PlaceOrder(
        [Description("Estimated order total in the household currency, e.g. 42.50.")] double estimatedTotal,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): mark list ordered; idempotent via ApprovalCoordinator ledger");
}
