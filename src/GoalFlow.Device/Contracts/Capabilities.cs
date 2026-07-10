namespace GoalFlow.Device.Contracts;

/// <summary>
/// Module-registry advertisement, device → cloud → ui (<c>type: "capabilities"</c>).
/// The device announces which modules it hosts so the cloud/UI can discover the
/// action space. Built by <c>Modules.Steering.CapabilityRegistry</c> from the
/// Semantic Kernel plugin collection — never hand-maintained.
/// </summary>
public sealed record CapabilitiesMessage
{
    public string Type { get; init; } = MessageTypes.Capabilities;

    public required IReadOnlyList<ModuleDescriptor> Modules { get; init; }
}

/// <summary>
/// One advertised module. <c>kind: "capability"</c> = an SK plugin whose
/// [KernelFunction]s the LLM calls; <c>kind: "steering"</c> = a harness module
/// that shapes/guards the run (safety filter, approval coordinator, clock, ...).
/// </summary>
public sealed record ModuleDescriptor
{
    public required string Name { get; init; }

    /// <summary>See <see cref="ModuleKinds"/>.</summary>
    public required string Kind { get; init; }

    public string? Description { get; init; }

    /// <summary>[KernelFunction]s for capability modules; null for steering modules.</summary>
    public IReadOnlyList<FunctionDescriptor>? Functions { get; init; }
}

/// <summary>One [KernelFunction] exposed by a capability module.</summary>
public sealed record FunctionDescriptor
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>True if invoking it changes the world (needs a proposal + tier).</summary>
    public bool SideEffecting { get; init; }

    /// <summary>Approval tier for side-effecting functions (see <see cref="ApprovalTiers"/>).</summary>
    public string? Tier { get; init; }
}

/// <summary>Module kinds carried on <see cref="ModuleDescriptor.Kind"/>.</summary>
public static class ModuleKinds
{
    public const string Capability = "capability";
    public const string Steering = "steering";
}
