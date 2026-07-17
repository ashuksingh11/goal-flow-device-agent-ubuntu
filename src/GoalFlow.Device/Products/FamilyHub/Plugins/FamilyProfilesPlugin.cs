using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (shared): who lives here — members, diets, dislikes, ages.
/// SK plugin, name "FamilyProfiles". Backed by data/family.json through
/// <see cref="IProductApiAdapter"/>.
///
/// NOTE: hard health constraints (allergies/medical) still arrive on the
/// dispatch's constraints.hard — profiles are grounding/preference input, never
/// the safety source of truth. The planner uses this to shape a menu or a guest
/// list; the SafetyFilter enforces the hard constraints regardless of what is here.
/// </summary>
[Description("Family member profiles: diets, dislikes, ages — who lives here.")]
public sealed class FamilyProfilesPlugin
{
    private readonly IProductApiAdapter _store;

    public FamilyProfilesPlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Lists family members with their dietary preferences, dislikes and ages.")]
    public async Task<string> GetProfiles(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("family", ct);
        return Json(doc["members"]);
    }

    [KernelFunction]
    [Description("Returns one member's dietary preferences, dislikes, age and notes.")]
    public async Task<string> GetMember(
        [Description("Member name, e.g. \"Aarav\".")] string name,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("family", ct);
        var member = doc["members"]?.AsArray()
            .Select(n => n!.AsObject())
            .FirstOrDefault(m => string.Equals(m["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));

        return member is null
            ? Json(new JsonObject { ["found"] = false, ["name"] = name })
            : Json(member.DeepClone());
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
