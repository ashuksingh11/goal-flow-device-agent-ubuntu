using System.ComponentModel;
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
    public Task<string> ListItems(CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): read inventory.json via MockWorldStore, resolved dates");

    [KernelFunction]
    [Description("Lists items that will expire within the given number of days from today — prime candidates for waste-rescue meals.")]
    public Task<string> GetExpiringItems(
        [Description("Look-ahead horizon in days from today, e.g. 3.")] int withinDays,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): filter by resolved expires_on <= today + withinDays");

    [KernelFunction]
    [Description("Checks which of the given ingredients are available in sufficient quantity; returns available vs missing.")]
    public Task<string> CheckAvailability(
        [Description("Ingredient names to check, e.g. [\"spinach\",\"rice\"].")] string[] ingredients,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): diff ingredients against inventory items");

    [KernelFunction]
    [SideEffect(Contracts.ApprovalTiers.Auto)]
    [Description("Marks a quantity of an item as used/consumed (e.g. after a planned dinner). Reversible bookkeeping.")]
    public Task<string> ConsumeItem(
        [Description("Inventory item name, e.g. \"spinach\".")] string name,
        [Description("Quantity consumed, in the item's unit.")] double quantity,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): decrement quantity, persist via MockWorldStore.SaveAsync");
}
