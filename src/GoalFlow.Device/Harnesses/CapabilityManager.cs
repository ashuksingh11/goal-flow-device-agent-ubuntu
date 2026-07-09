namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Sense-phase harness: static registry of the local + cloud APIs available
/// to this device. Build effort: HONEST STUB — a hardcoded list; a real
/// device would discover/negotiate capabilities.
/// </summary>
public interface ICapabilityManager
{
    /// <summary>All capabilities registered on this device.</summary>
    IReadOnlyList<Capability> ListCapabilities();

    /// <summary>True when the capability id is registered and usable.</summary>
    bool IsAvailable(string capabilityId);
}

/// <summary>Where a capability is served from.</summary>
public enum CapabilityKind
{
    Local,
    Cloud,
}

/// <summary>One registered capability, e.g. ("inventory.read", Local).</summary>
public sealed record Capability
{
    public required string Id { get; init; }

    public required CapabilityKind Kind { get; init; }

    public required string Description { get; init; }
}

/// <summary>
/// Honest stub: fixed registry (inventory.read, calendar.read, recipes.read,
/// shopping_list.read/write, reminders.read/write, llm.plan [cloud]).
/// </summary>
public sealed class StaticCapabilityManager : ICapabilityManager
{
    public IReadOnlyList<Capability> ListCapabilities() =>
        // TODO: return the hardcoded registry above.
        throw new NotImplementedException("Design stub.");

    public bool IsAvailable(string capabilityId) =>
        throw new NotImplementedException("Design stub.");
}
