using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// Reads one boolean flag out of <c>data/device_state.json</c> — is the account
/// signed in, is there internet, is SmartThings reachable.
///
/// <para>
/// ONE probe class per flag would be nine near-identical files, so this is
/// constructed per flag with the message a person needs to act on. The remediation
/// text is the point: "SmartThings is disconnected" tells someone what to DO, where
/// "precheck smartthings_connected failed" tells them only that a computer is unhappy.
/// </para>
/// </summary>
public sealed class DeviceStateProbe : IPrecheckProbe
{
    private readonly IProductApiAdapter _store;
    private readonly string _flag;
    private readonly string _remediation;

    public DeviceStateProbe(IProductApiAdapter store, string id, string flag, string remediation)
    {
        _store = store;
        Id = id;
        _flag = flag;
        _remediation = remediation;
    }

    public string Id { get; }

    public async Task<PrecheckResult> RunAsync(string? argument, CancellationToken ct = default)
    {
        var state = await _store.LoadResolvedAsync("device_state", ct);
        return state[_flag]?.GetValue<bool>() == true
            ? PrecheckResult.Pass(Id)
            : PrecheckResult.Fail(Id, _remediation);
    }
}

/// <summary>
/// Is a named appliance reachable? Parameterized — the binding says
/// <c>appliance_online:oven</c>, so one probe serves the whole product.
///
/// <para>
/// This is the one that earns the component: preheating an unplugged oven passes
/// safety (nothing forbids it) and passes approval (the user said yes), and then
/// simply doesn't happen. Only a precheck catches it, and only if it runs at
/// actuation — the oven can go down between planning and approval, which is
/// exactly when the delay is.
/// </para>
/// </summary>
public sealed class ApplianceOnlineProbe : IPrecheckProbe
{
    private readonly IProductApiAdapter _store;

    public ApplianceOnlineProbe(IProductApiAdapter store) => _store = store;

    public string Id => "appliance_online";

    public async Task<PrecheckResult> RunAsync(string? argument, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            // A binding of bare "appliance_online" names no appliance. Skipped, not
            // failed: it is a config mistake, and failing would block a call for a
            // reason that has nothing to do with the world.
            return PrecheckResult.Skip(Id, "no appliance named — bind it as appliance_online:<id>");
        }

        var state = await _store.LoadResolvedAsync("device_state", ct);
        var online = state["appliances_online"]?[argument];
        if (online is null)
        {
            return PrecheckResult.Fail(Id, $"the {argument} is not a known appliance on this device");
        }

        return online.GetValue<bool>()
            ? PrecheckResult.Pass(Id)
            : PrecheckResult.Fail(Id, $"the {argument} is offline — reconnect it and this will resume");
    }
}
