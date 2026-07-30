using System.Text.Json;
using System.Text.Json.Nodes;
using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// THIS product's policy resolution: the household envelope narrows a goal's cap.
///
/// <para>
/// The account sends two numbers — this goal's <c>budget_cap</c> ($120 for a grocery
/// week, $200 for a party, $1500 for a trip) and the household's
/// <c>budget_envelope</c> ($600 a month, shared by everything). The device holds the
/// third: how much of the envelope has already been SPENT. The effective ceiling is
/// <c>min(budget_cap, envelope − spent)</c>, and that is what gets armed.
/// </para>
///
/// <para>
/// This is what makes two goals share a wallet. Approve the party's order and the
/// grocery goal's headroom shrinks the next time its policy is resolved — on its own
/// approval, or on the next day tick — because the money is gone. Per-goal caps alone
/// never noticed each other.
/// </para>
///
/// <para>
/// It only ever TIGHTENS: a goal whose own cap is already below the remaining envelope
/// keeps its cap, and an exhausted envelope floors at zero rather than going negative
/// (a negative ceiling would make every order look like an over-spend by a stranger
/// amount instead of plainly "there is nothing left").
/// </para>
/// </summary>
public sealed class FamilyHubPolicyResolver : IPolicyResolver
{
    private readonly IProductApiAdapter _store;

    public FamilyHubPolicyResolver(IProductApiAdapter store) => _store = store;

    public async Task<JsonObject> ResolveAsync(JsonObject dispatched, CancellationToken ct = default)
    {
        var resolved = (JsonObject)dispatched.DeepClone();
        if (resolved["budget_envelope"] is not JsonObject envelope
            || envelope["cap"] is not { } capNode
            || capNode.GetValueKind() != JsonValueKind.Number)
        {
            return resolved;
        }

        var budget = await _store.LoadResolvedAsync("budget", ct);
        var spent = budget["spent"]?.GetValue<double>() ?? 0;
        var remaining = Math.Max(0, Math.Round(capNode.GetValue<double>() - spent, 2));

        // What is left of the pool, reported alongside the pool itself: the Budget
        // module surfaces this to the planner, and a plan that cannot see the shared
        // headroom will keep proposing baskets the gate then refuses.
        envelope["remaining"] = remaining;
        envelope["spent"] = Math.Round(spent, 2);

        var goalCap = resolved["budget_cap"];
        var effective = goalCap is not null && goalCap.GetValueKind() == JsonValueKind.Number
            ? Math.Min(goalCap.GetValue<double>(), remaining)
            : remaining;
        resolved["budget_cap"] = effective;
        return resolved;
    }
}
