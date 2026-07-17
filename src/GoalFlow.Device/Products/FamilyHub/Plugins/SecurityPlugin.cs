using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (home security): doors, cameras, alarm. SK plugin, name
/// "Security". Backed by data/security.json through <see cref="IProductApiAdapter"/>.
///
/// This is the vacation-prep centrepiece — "get the house ready, we're away" turns
/// into lock every door and arm the cameras. Locking is A0/Auto (always safe, always
/// reversible); ARMING is Light (privacy-sensitive, worth a glance) and gated by the
/// camera/AI-vision prechecks so a goal can't claim the house is watched when the
/// camera is dark.
/// </summary>
[Description("Home security: lock doors, arm cameras and the alarm, check the security posture.")]
public sealed class SecurityPlugin
{
    private readonly IProductApiAdapter _store;

    public SecurityPlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Returns the current security posture: which doors are locked, which cameras are armed, and the alarm state.")]
    public async Task<string> GetSecurityStatus(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("security", ct);
        return Json(new JsonObject
        {
            ["doors"] = doc["doors"]?.DeepClone(),
            ["cameras"] = doc["cameras"]?.DeepClone(),
            ["alarm"] = doc["alarm"]?.DeepClone()
        });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Locks every exterior door. Safe and reversible — auto tier.")]
    public async Task<string> LockAllDoors(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("security", ct);
        var locked = new JsonArray();
        foreach (var door in (doc["doors"]?.AsArray() ?? []).Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            door["locked"] = true;
            locked.Add(door["name"]?.GetValue<string>() ?? door["id"]?.GetValue<string>());
        }
        await _store.SaveAsync("security", doc, ct);
        return Json(new JsonObject { ["status"] = "locked", ["doors"] = locked });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Arms the alarm and cameras in the given mode (e.g. \"away\"). Requires the cameras to be operational.")]
    public async Task<string> ArmSecurity(
        [Description("Alarm mode, e.g. \"away\" or \"night\".")] string mode,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("security", ct);
        foreach (var camera in (doc["cameras"]?.AsArray() ?? []).Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            camera["armed"] = true;
        }
        var alarm = doc["alarm"]?.AsObject() ?? new JsonObject();
        alarm["armed"] = true;
        alarm["mode"] = mode;
        doc["alarm"] = alarm;
        await _store.SaveAsync("security", doc, ct);

        var cameraCount = doc["cameras"]?.AsArray().Count ?? 0;
        return Json(new JsonObject
        {
            ["status"] = "armed",
            ["mode"] = mode,
            ["cameras_armed"] = cameraCount
        });
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
