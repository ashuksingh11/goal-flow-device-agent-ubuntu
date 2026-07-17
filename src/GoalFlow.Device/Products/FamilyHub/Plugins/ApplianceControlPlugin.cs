using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Steering;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Capabilities;

/// <summary>
/// CAPABILITY MODULE (shared): other appliances via SmartThings — oven,
/// dishwasher, robot vacuum, lights. SK plugin, name "Appliance".
/// SIGNATURES ONLY in v2-M0; the guest_dinner domain (prep timeline,
/// "preheat oven at 18:30") fleshes this out in a later milestone.
/// Scheduled actions are checked by the SafetyFilter against quiet_hours and
/// the unattended-appliance rule in constraints.hard.
/// </summary>
[Description("Controls SmartThings appliances: oven, dishwasher, vacuum, lights.")]
public sealed class ApplianceControlPlugin
{
    private readonly MockWorldStore _store;

    public ApplianceControlPlugin(MockWorldStore store) => _store = store;

    [KernelFunction]
    [Description("Lists the appliances SmartThings can reach and their current state.")]
    public async Task<string> ListAppliances(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("appliances", ct);
        return Json(doc["appliances"]);
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Schedules the oven to preheat to a temperature at a time. Checked against quiet hours and unattended-use rules.")]
    public async Task<string> PreheatOven(
        [Description("Target temperature in C, e.g. 200.")] int targetC,
        [Description("ISO local date-time, e.g. \"2026-07-11T18:30\".")] string atTime,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("appliances", ct);
        var oven = FindAppliance(doc, "oven");
        var action = AddScheduledAction(doc, "preheat_oven", atTime, new JsonObject
        {
            ["appliance"] = oven["id"]?.GetValue<string>() ?? "oven",
            ["target_c"] = targetC
        });
        await _store.SaveAsync("appliances", doc, ct);
        return Json(new JsonObject
        {
            ["status"] = "scheduled",
            ["action_id"] = action,
            ["appliance"] = oven["name"]?.GetValue<string>() ?? "oven",
            ["target_c"] = targetC,
            ["at_time"] = atTime
        });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Runs an appliance program at a time (e.g. dishwasher eco cycle, vacuum the kitchen).")]
    public async Task<string> RunProgram(
        [Description("Appliance id or name, e.g. \"dishwasher\".")] string appliance,
        [Description("Program name, e.g. \"eco\".")] string program,
        [Description("ISO local date-time, e.g. \"2026-07-11T21:30\".")] string atTime,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("appliances", ct);
        var match = FindAppliance(doc, appliance);
        var supported = match["programs"]?.AsArray()
            .Any(p => string.Equals(p?.GetValue<string>(), program, StringComparison.OrdinalIgnoreCase)) == true;
        if (!supported)
        {
            throw new InvalidOperationException($"Appliance '{appliance}' does not support program '{program}'.");
        }

        var action = AddScheduledAction(doc, "run_program", atTime, new JsonObject
        {
            ["appliance"] = match["id"]?.GetValue<string>() ?? appliance,
            ["program"] = program
        });
        await _store.SaveAsync("appliances", doc, ct);
        return Json(new JsonObject
        {
            ["status"] = "scheduled",
            ["action_id"] = action,
            ["appliance"] = match["name"]?.GetValue<string>() ?? appliance,
            ["program"] = program,
            ["at_time"] = atTime
        });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Moves an item to the fridge's defrost/thaw workflow (e.g. 'defrost the paneer tonight').")]
    public async Task<string> Defrost(
        [Description("Item to defrost, e.g. \"paneer\".")] string item,
        [Description("ISO local date-time, e.g. \"2026-07-10T20:00\".")] string atTime,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("appliances", ct);
        var fridge = FindAppliance(doc, "fridge");
        var action = AddScheduledAction(doc, "defrost", atTime, new JsonObject
        {
            ["appliance"] = fridge["id"]?.GetValue<string>() ?? "fridge",
            ["item"] = item
        });
        await _store.SaveAsync("appliances", doc, ct);
        return Json(new JsonObject
        {
            ["status"] = "scheduled",
            ["action_id"] = action,
            ["item"] = item,
            ["at_time"] = atTime
        });
    }

    private static JsonObject FindAppliance(JsonObject doc, string idOrName)
    {
        return doc["appliances"]?.AsArray()
            .Select(n => n!.AsObject())
            .FirstOrDefault(a =>
                string.Equals(a["id"]?.GetValue<string>(), idOrName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a["name"]?.GetValue<string>(), idOrName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Appliance '{idOrName}' was not found.");
    }

    private static string AddScheduledAction(JsonObject doc, string type, string atTime, JsonObject details)
    {
        var actions = doc["scheduled_actions"]?.AsArray();
        if (actions is null)
        {
            actions = [];
            doc["scheduled_actions"] = actions;
        }

        var id = $"app-{actions.Count + 1:000}";
        actions.Add(new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["at_time"] = atTime,
            ["details"] = details,
            ["status"] = "scheduled"
        });
        return id;
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
