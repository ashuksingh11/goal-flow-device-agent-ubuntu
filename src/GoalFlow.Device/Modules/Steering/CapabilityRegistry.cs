using System.Reflection;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Capabilities;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Steering;

/// <summary>
/// Marks a [KernelFunction] as SIDE-EFFECTING with its approval tier.
/// Read by <see cref="CapabilityRegistry"/> for the capabilities advertisement
/// and by the SafetyFilter/ApprovalCoordinator to decide whether a pending
/// call must be frozen into a proposal instead of executing directly.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SideEffectAttribute : Attribute
{
    public SideEffectAttribute(string tier) => Tier = tier;

    /// <summary>See <see cref="ApprovalTiers"/> (auto / light / firm).</summary>
    public string Tier { get; }
}

/// <summary>
/// HARNESS MODULE: Capability Registry.
/// The device's toolbox is DISCOVERED, not hand-listed: this module walks the
/// Semantic Kernel plugin collection (<c>kernel.Plugins</c> → KernelFunction
/// metadata) plus the fixed set of steering modules, and builds the
/// <c>capabilities</c> message the device sends right after hello_ack.
/// Adding a new domain = registering new SK plugins; the advertisement,
/// the planner's action space, and the UI's module view all follow.
/// </summary>
public sealed class CapabilityRegistry
{
    /// <summary>
    /// Builds the wire-ready capabilities message: one capability
    /// <see cref="ModuleDescriptor"/> per SK plugin (functions from
    /// KernelFunctionMetadata; side_effecting/tier from
    /// <see cref="SideEffectAttribute"/> via reflection), plus one steering
    /// descriptor per harness module (Safety, Approval, Grounding, Clock,
    /// MonitorAdapt, Trace, CapabilityRegistry itself).
    /// </summary>
    public CapabilitiesMessage BuildCapabilitiesMessage(Kernel kernel)
    {
        var modules = kernel.Plugins.Select(DescribePlugin).Concat(SteeringModules).ToArray();
        return new CapabilitiesMessage { Modules = modules };
    }

    /// <summary>Descriptor for one plugin — unit-testable slice of the walk above.</summary>
    public ModuleDescriptor DescribePlugin(KernelPlugin plugin)
    {
        var functions = plugin.GetFunctionsMetadata()
            .Select(f =>
            {
                var tier = GetSideEffectTier(plugin.Name, f.Name);
                return new FunctionDescriptor
                {
                    Name = f.Name,
                    Description = f.Description,
                    SideEffecting = tier is not null,
                    Tier = tier
                };
            })
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();

        return new ModuleDescriptor
        {
            Name = plugin.Name,
            Kind = ModuleKinds.Capability,
            Description = PluginType(plugin.Name)?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? plugin.Description,
            Functions = functions
        };
    }

    public static string? GetSideEffectTier(string module, string function)
    {
        var method = PluginType(module)?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => string.Equals(m.Name, function, StringComparison.OrdinalIgnoreCase));
        return method?.GetCustomAttribute<SideEffectAttribute>()?.Tier;
    }

    public static bool IsSideEffecting(string module, string function)
        => GetSideEffectTier(module, function) is not null;

    private static Type? PluginType(string module) => module switch
    {
        "Inventory" => typeof(InventoryPlugin),
        "Calendar" => typeof(CalendarPlugin),
        "Recipes" => typeof(RecipePlugin),
        "ShoppingList" => typeof(ShoppingListPlugin),
        "Reminders" => typeof(ReminderPlugin),
        "Guests" => typeof(GuestsPlugin),
        "Appliance" => typeof(ApplianceControlPlugin),
        "FamilyProfiles" => typeof(FamilyProfilesPlugin),
        "Budget" => typeof(BudgetPlugin),
        "Notify" => typeof(NotifyPlugin),
        _ => null
    };

    /// <summary>The steering half of the advertisement (fixed, code-defined).</summary>
    public static IReadOnlyList<ModuleDescriptor> SteeringModules { get; } =
    [
        new() { Name = "Safety", Kind = ModuleKinds.Steering, Description = "deterministic hard-constraint filter (SK IFunctionInvocationFilter)" },
        new() { Name = "Approval", Kind = ModuleKinds.Steering, Description = "tiered HITL consent coordinator (auto/light/firm)" },
        new() { Name = "Grounding", Kind = ModuleKinds.Steering, Description = "world-state context assembler for the planner" },
        new() { Name = "Scheduler", Kind = ModuleKinds.Steering, Description = "generic clock + temporal sequencing (real or simulated)" },
        new() { Name = "MonitorAdapt", Kind = ModuleKinds.Steering, Description = "material-change detection and adaptation proposals" },
        new() { Name = "Trace", Kind = ModuleKinds.Steering, Description = "structured logging + agent_event streaming" },
        new() { Name = "CapabilityRegistry", Kind = ModuleKinds.Steering, Description = "module discovery/advertisement (this message)" },
    ];
}
