using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (shared): reminders/notes on the Hub screen.
/// SK plugin, name "Reminders". Backed by data/reminders.json (due dates
/// stored as day offsets). Creating a reminder is the canonical AUTO-tier
/// side-effect: cheap and reversible, so the agent may just do it.
/// </summary>
[Description("Family reminders and notes shown on the Hub.")]
public sealed class ReminderPlugin
{
    private readonly MockWorldStore _store;

    public ReminderPlugin(MockWorldStore store) => _store = store;

    [KernelFunction]
    [Description("Lists all active reminders with their due date/time.")]
    public async Task<string> List(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("reminders", ct);
        return Json(doc["reminders"]);
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Creates a reminder (e.g. 'defrost the paneer tonight'). Cheap and reversible — auto tier.")]
    public async Task<string> Create(
        [Description("Reminder text.")] string title,
        [Description("Due ISO date, e.g. \"2026-07-14\".")] string date,
        [Description("Optional due time HH:mm.")] string? time = null,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("reminders", ct);
        var reminders = doc["reminders"]!.AsArray();
        var id = $"rem-{reminders.Count + 1:000}";
        reminders.Add(new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["due_in_days"] = _store.OffsetFromToday(date),
            ["time"] = time,
            ["active"] = true
        });
        await _store.SaveAsync("reminders", doc, ct);
        return Json(new JsonObject { ["status"] = "created", ["id"] = id });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Deletes a reminder by id.")]
    public async Task<string> Delete(
        [Description("Reminder id, e.g. \"rem-001\".")] string id,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("reminders", ct);
        var kept = doc["reminders"]!.AsArray()
            .Where(n => !string.Equals(n!["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
            .Select(n => n!.DeepClone())
            .ToArray();
        doc["reminders"] = new JsonArray(kept);
        await _store.SaveAsync("reminders", doc, ct);
        return Json(new JsonObject { ["status"] = "deleted", ["id"] = id });
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
