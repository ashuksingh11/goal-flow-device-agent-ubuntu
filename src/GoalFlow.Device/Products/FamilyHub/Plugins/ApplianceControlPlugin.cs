using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (shared): the home's appliances over SmartThings — oven,
/// dishwasher, washing machine, fridge, TV and robot vacuum. SK plugin, name
/// "Appliance". Scheduled actions are checked by the SafetyFilter against the window
/// rules in constraints.hard (peak tariff on an energy goal, the away window on a trip).
///
/// <para>
/// v7 made the description honest. It claimed a vacuum from v2 onward while the world
/// held none, which is not a harmless comment: the [Description] is what the planner
/// reads to decide what it may reach for, so an over-claim invites a proposal the device
/// then fails to execute. The vacuum exists now (data/appliances.json), and the list
/// here matches the seed.
/// </para>
/// </summary>
[Description("Controls the home's SmartThings appliances: oven, dishwasher, washing machine, fridge, TV and robot vacuum.")]
public sealed class ApplianceControlPlugin
{
    private readonly IProductApiAdapter _store;

    public ApplianceControlPlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Lists the appliances SmartThings can reach, their current state, their supported programs and their energy draw.")]
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
    [Description("Runs a SmartThings appliance program at a time — a dishwasher eco cycle, a washer run, or a robot vacuum clean.")]
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
