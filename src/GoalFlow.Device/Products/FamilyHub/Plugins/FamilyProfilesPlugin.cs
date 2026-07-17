using System.ComponentModel;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (shared): who lives here — members, diets, dislikes.
/// SK plugin, name "FamilyProfiles". SIGNATURES ONLY in v2-M0 (data file
/// data/family.json arrives with the guest_dinner milestone).
/// NOTE: hard health constraints (allergies/medical) still arrive on the
/// dispatch's constraints.hard — profiles are grounding/preference input,
/// never the safety source of truth.
/// </summary>
[Description("Family member profiles: diets, allergies, dislikes, ages.")]
[Unavailable("v2-M0 skeleton — every method throws NotImplementedException")]
public sealed class FamilyProfilesPlugin
{
    private readonly IProductApiAdapter _store;

    public FamilyProfilesPlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Lists family members with their dietary preferences, dislikes and ages.")]
    public Task<string> GetProfiles(CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton");

    [KernelFunction]
    [Description("Returns one member's dietary constraints and preferences.")]
    public Task<string> GetMember(
        [Description("Member name, e.g. \"Aarav\".")] string name,
        CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton");
}
