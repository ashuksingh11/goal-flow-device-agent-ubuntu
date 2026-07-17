using System.ComponentModel;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (shared): grocery/household budget awareness.
/// SK plugin, name "Budget". SIGNATURES ONLY in v2-M0. Read-only: the
/// planner uses it to ESTIMATE; enforcement of budget_cap is the
/// SafetyFilter's job at PlaceOrder time ("LLM plans, code checks").
/// </summary>
[Description("Grocery budget status and cost estimation.")]
public sealed class BudgetPlugin
{
    private readonly IProductApiAdapter _store;

    public BudgetPlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Returns the budget period, cap, and how much has been spent so far.")]
    public Task<string> GetBudgetStatus(CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton");

    [KernelFunction]
    [Description("Estimates the total cost of a set of grocery items.")]
    public Task<string> EstimateCost(
        [Description("Item names to price, e.g. [\"lentils\",\"pasta\"].")] string[] items,
        CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton");
}
