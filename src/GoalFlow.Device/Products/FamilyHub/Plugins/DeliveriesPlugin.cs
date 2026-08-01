using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (v7): standing deliveries — the subscriptions and scheduled drops
/// that keep arriving whether or not anyone is home. SK plugin, name "Deliveries".
/// Backed by data/deliveries.json through <see cref="IProductApiAdapter"/>.
///
/// <para>
/// WHY A2. Holding a delivery is an outward-facing commitment to a third party with a
/// date attached — the family cannot un-tell the dairy after the fact, and a wrongly held
/// drop is a week without vegetables. That is a different kind of act from adding milk to
/// a list (<see cref="ShoppingListPlugin"/>, A1), so it gets a different grade and its
/// own approval on the card rather than riding the batch.
/// </para>
///
/// <para>
/// ESSENTIAL IS THE HOUSEHOLD'S CALL, NOT THE MODEL'S. The seed marks a repeat
/// prescription essential, and <see cref="Hold"/> refuses it outright rather than
/// trusting the planner to have read the note. This is deterministic code saying no —
/// the same shape as the Safety engine, for the same reason: a plan that pauses someone's
/// medication to tidy up the porch is not a plan anyone should have to catch by reading.
/// </para>
/// </summary>
[Description("Standing deliveries and subscriptions: what is due to arrive, and holding or resuming them.")]
public sealed class DeliveriesPlugin
{
    private readonly IProductApiAdapter _store;

    public DeliveriesPlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Lists standing deliveries and subscriptions: what is due, when the next one arrives, whether it is essential, and whether it is currently held.")]
    public async Task<string> ListDeliveries(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("deliveries", ct);
        return Json(doc["deliveries"]);
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Firm)]
    [Description("Pauses a non-essential delivery until a date, e.g. while the family is away. Essential deliveries such as medication cannot be held.")]
    public async Task<string> Hold(
        [Description("Delivery id or name, e.g. \"milk subscription\".")] string delivery,
        [Description("ISO date to resume on, e.g. \"2026-08-04\".")] string until,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("deliveries", ct);
        var match = Find(doc, delivery);

        if (match["essential"]?.GetValue<bool>() == true)
        {
            // Thrown, not returned as a soft result: a refusal the planner can read as
            // "done" is a refusal that lands on the card as a completed step.
            throw new InvalidOperationException(
                $"'{match["name"]}' is an essential delivery and cannot be held. {match["note"]}");
        }

        match["held"] = true;
        match["held_until"] = until;
        await _store.SaveAsync("deliveries", doc, ct);
        return Json(new JsonObject
        {
            ["status"] = "held",
            ["delivery"] = match["name"]?.GetValue<string>() ?? delivery,
            ["vendor"] = match["vendor"]?.GetValue<string>(),
            ["until"] = until
        });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Resumes a held delivery — the return-readiness step after the family is back.")]
    public async Task<string> Resume(
        [Description("Delivery id or name, e.g. \"milk subscription\".")] string delivery,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("deliveries", ct);
        var match = Find(doc, delivery);
        match["held"] = false;
        match.Remove("held_until");
        await _store.SaveAsync("deliveries", doc, ct);
        return Json(new JsonObject
        {
            ["status"] = "resumed",
            ["delivery"] = match["name"]?.GetValue<string>() ?? delivery
        });
    }

    private static JsonObject Find(JsonObject doc, string idOrName)
        => doc["deliveries"]?.AsArray()
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            .FirstOrDefault(d =>
                string.Equals(d["id"]?.GetValue<string>(), idOrName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d["name"]?.GetValue<string>(), idOrName, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"Delivery '{idOrName}' was not found.");

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
