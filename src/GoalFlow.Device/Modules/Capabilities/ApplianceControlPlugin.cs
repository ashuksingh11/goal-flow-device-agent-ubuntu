using System.ComponentModel;
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
    private readonly IClock _clock;

    public ApplianceControlPlugin(IClock clock) => _clock = clock;

    [KernelFunction]
    [Description("Lists the appliances SmartThings can reach and their current state.")]
    public Task<string> ListAppliances(CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton; guest_dinner milestone");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Schedules the oven to preheat to a temperature at a time. Checked against quiet hours and unattended-use rules.")]
    public Task<string> PreheatOven(
        [Description("Target temperature in °C, e.g. 200.")] int temperatureCelsius,
        [Description("ISO date of the preheat.")] string date,
        [Description("Start time HH:mm, e.g. \"18:30\".")] string time,
        CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton; guest_dinner milestone");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Runs an appliance program at a time (e.g. dishwasher eco cycle, vacuum the kitchen).")]
    public Task<string> RunProgram(
        [Description("Appliance id or name, e.g. \"dishwasher\".")] string appliance,
        [Description("Program name, e.g. \"eco\".")] string program,
        [Description("ISO date.")] string date,
        [Description("Start time HH:mm.")] string time,
        CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton; guest_dinner milestone");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Moves an item to the fridge's defrost/thaw workflow (e.g. 'defrost the paneer tonight').")]
    public Task<string> Defrost(
        [Description("Item to defrost, e.g. \"paneer\".")] string item,
        [Description("ISO date to defrost on.")] string date,
        CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton; guest_dinner milestone");
}
