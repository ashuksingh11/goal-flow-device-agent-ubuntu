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
    private readonly SafetyPolicy _policy;
    private readonly IReadOnlyList<IDomainObserver> _observers;

    /// <summary>The registered capabilities, IN THE PRODUCT PACK'S ORDER (significant — see the descriptor).</summary>
    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; }

    public CapabilityManager(
        IReadOnlyList<CapabilityDescriptor> descriptors,
        SafetyPolicy policy,
        IEnumerable<IDomainObserver>? observers = null)
    {
        Descriptors = descriptors;
        _policy = policy;
        // The domains this device advertises ARE the ones it can sustain, so they
        // are derived from the registered observers rather than hand-listed — the
        // same rule as the capability catalog: add an observer, and the cloud
        // learns the domain exists.
        _observers = observers?.ToArray() ?? [];
        _byName = descriptors.ToDictionary(d => d.Name, StringComparer.Ordinal);

        // Fail at STARTUP, not at the moment a prohibited action is proposed.
        // Every override is resolved here so a policy typo cannot lie dormant
        // until the one run where it matters.
        foreach (var descriptor in descriptors)
        {
            foreach (var method in descriptor.Instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                _ = GradeOf(descriptor.Name, method.Name);
            }
        }
    }

    /// <summary>
    /// The automation grade of a function: what the code intrinsically declares
    /// via <see cref="SideEffectAttribute"/>, possibly made STRICTER by the
    /// product's policy. Null for read-only functions — they are not actions.
    /// Throws if policy tries to weaken it (the ratchet; see SafetyPolicy.GradeFor).
    /// </summary>
    public AutomationGrade? GradeOf(string module, string function)
        => _policy.GradeFor(module, function, Grades.FromTier(GetSideEffectTier(module, function)));

    /// <summary>
    /// Actions the planner may legitimately propose: side-effecting, available,
    /// and NOT prohibited.
    ///
    /// <para>
    /// This is the first of AX's two enforcement points. A prohibited capability
    /// is never offered as a proposal target, and the filter also blocks it
    /// unconditionally at invocation — belt and braces, because the two answer
    /// different questions ("should the model be told it may do this?" vs "is it
    /// about to actually happen?") and only the second one is load-bearing if the
    /// model improvises.
    /// </para>
    /// </summary>
    public bool IsProposable(string module, string function)
    {
        if (!_byName.TryGetValue(module, out var descriptor) || !descriptor.Available)
        {
            return false;
        }

        var grade = GradeOf(module, function);
        return grade is not null && grade != AutomationGrade.AX;
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
        return new CapabilitiesMessage
        {
            Modules = modules,
            Domains = _observers
                .Select(o => new DomainDescriptor { Id = o.Domain, Hint = o.Hint })
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToArray()
        };
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

    /// <summary>
    /// THE PLANNER'S TOOL SET: every non-side-effecting function of every
    /// AVAILABLE capability, in the product pack's registration order. Replaces
    /// a hand-written 13-entry whitelist that had to be edited by hand whenever
    /// a plugin gained a read function — a fourth copy of the plugin catalog,
    /// and the one most likely to rot silently.
    ///
    /// <para>
    /// Two subtleties, both load-bearing (the M0 gate diffs this list against
    /// what the whitelist produced, byte for byte):
    /// </para>
    /// <para>
    /// 1. AVAILABILITY, not just side-effect-freedom. The stub plugins' reads
    ///    (FamilyProfiles, Budget) are indistinguishable from real ones by
    ///    reflection, so "every read" would hand the model 17 tools — 4 of which
    ///    throw. See <see cref="UnavailableAttribute"/>.
    /// </para>
    /// <para>
    /// 2. ORDER. This is the tools array the model receives, so the order is
    ///    part of the prompt. It comes from the pack's descriptor order, then
    ///    each plugin's own metadata order — deliberately NOT sorted, because
    ///    the capabilities advertisement sorts alphabetically and this does not.
    /// </para>
    /// </summary>
    public IReadOnlyList<KernelFunction> GetGroundingFunctions(Kernel kernel)
    {
        var functions = new List<KernelFunction>();
        foreach (var capability in Descriptors)
        {
            if (!capability.Available || !kernel.Plugins.TryGetPlugin(capability.Name, out var plugin) || plugin is null)
            {
                continue;
            }

            foreach (var metadata in plugin.GetFunctionsMetadata())
            {
                if (GetSideEffectTier(capability.Name, metadata.Name) is null)
                {
                    functions.Add(plugin[metadata.Name]);
                }
            }
        }

        return functions;
    }

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
