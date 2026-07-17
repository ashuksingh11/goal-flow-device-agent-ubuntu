using System.Reflection;
using GoalFlow.Device.Contracts;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Harness;

/// <summary>
/// HARNESS COMPONENT 1: Capability Manager.
///
/// The device's toolbox is DISCOVERED, not hand-listed: this walks the Semantic
/// Kernel plugin collection (<c>kernel.Plugins</c> → KernelFunctionMetadata,
/// plus <see cref="SideEffectAttribute"/> read off the registered instance) and
/// builds the <c>capabilities</c> message sent right after hello_ack. Adding a
/// domain = adding plugins to the product pack; the advertisement, the planner's
/// action space and the UI's module view all follow.
///
/// <para>
/// The registry used to CLAIM that while contradicting it: a hardcoded
/// name-to-Type switch listed all ten Family Hub plugin types, so a new plugin
/// meant editing the harness, and the "generic" core imported the product
/// namespace. Now the product pack hands over
/// <see cref="CapabilityDescriptor"/>s and reflection reads each live instance's
/// own type. This file no longer knows any product type exists.
/// </para>
/// </summary>
public sealed class CapabilityManager
{
    private readonly Dictionary<string, CapabilityDescriptor> _byName;

    /// <summary>The registered capabilities, IN THE PRODUCT PACK'S ORDER (significant — see the descriptor).</summary>
    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; }

    public CapabilityManager(IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        Descriptors = descriptors;
        _byName = descriptors.ToDictionary(d => d.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds the wire-ready capabilities message: one capability
    /// <see cref="ModuleDescriptor"/> per SK plugin (functions from
    /// KernelFunctionMetadata; side_effecting/tier from
    /// <see cref="SideEffectAttribute"/> via reflection), plus one steering
    /// descriptor per harness module.
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

    /// <summary>
    /// The approval tier of a function, or null if it is read-only. Reflects over
    /// the REGISTERED INSTANCE's type — this is what replaced the hardcoded
    /// name-to-Type switch.
    /// </summary>
    public string? GetSideEffectTier(string module, string function)
        => PluginType(module)?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => string.Equals(m.Name, function, StringComparison.OrdinalIgnoreCase))
            ?.GetCustomAttribute<SideEffectAttribute>()?.Tier;

    public bool IsSideEffecting(string module, string function)
        => GetSideEffectTier(module, function) is not null;

    /// <summary>The registered instance's type, or null for an unknown module.</summary>
    private Type? PluginType(string module)
        => _byName.TryGetValue(module, out var descriptor) ? descriptor.Instance.GetType() : null;

    /// <summary>
    /// The steering half of the advertisement (fixed, code-defined).
    ///
    /// NOTE: these names are WIRE VALUES the cloud and UI read, so they still say
    /// "CapabilityRegistry" and the other v2 module names even though the code is
    /// now organised around the five v3 components. Renaming them is a CONTRACT
    /// change and belongs with the rest of contract v3, landed in one pass across
    /// every mirror — not smuggled in by a refactor. PRODUCT-DEBT(M6).
    /// </summary>
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
