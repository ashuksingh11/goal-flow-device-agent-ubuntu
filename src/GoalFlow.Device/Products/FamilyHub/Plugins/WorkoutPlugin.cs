using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (v7): the household's activity data — the weekly training
/// routine and the last few days of steps and calories. SK plugin, name "Workout".
/// Backed by data/workout.json through <see cref="IProductApiAdapter"/>.
///
/// <para>
/// READ-ONLY BY CONSTRUCTION. Every function is A0: nothing here writes, schedules or
/// spends, so there is no <c>[SideEffect]</c> anywhere in the file and nothing for the
/// approval gate to hold. It exists so a meal plan can be shaped around what the
/// household actually did rather than around a preference someone typed once.
/// </para>
///
/// <para>
/// THIS IS EVIDENCE, NOT POLICY. The cloud resolves a soft "workout-friendly"
/// preference for meal goals; this is what the planner reads to act on it. Activity
/// data can never block a dinner — a calorie ceiling that could would be a medical
/// constraint, and those arrive on <c>constraints.hard</c> from the account, the same
/// as allergens. Same split as <see cref="FamilyProfilesPlugin"/>: grounding input,
/// never the safety source of truth.
/// </para>
/// </summary>
[Description("Household activity data: the weekly workout routine, and recent steps and calories burned.")]
public sealed class WorkoutPlugin
{
    private readonly IProductApiAdapter _store;

    public WorkoutPlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Returns the household's weekly workout routine and daily step target — which days are hard training days and which are rest days.")]
    public async Task<string> GetWeeklyRoutine(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("workout", ct);
        return Json(doc["routine"]);
    }

    [KernelFunction]
    [Description("Returns the last few days of logged activity — steps, calories burned and what was done each day, with dates.")]
    public async Task<string> GetRecentActivity(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("workout", ct);
        // Returned VERBATIM, the way Appliance.ListAppliances returns per-appliance kWh:
        // the planner does the arithmetic it needs against real numbers, and a summary
        // computed here would be this file deciding what "a hard day" means on its behalf.
        return Json(doc["recent"]);
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
