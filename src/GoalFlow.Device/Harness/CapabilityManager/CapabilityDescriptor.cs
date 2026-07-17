namespace GoalFlow.Device.Harness;

/// <summary>
/// One registered capability plugin: the name it is advertised and invoked
/// under, and the live instance behind it.
///
/// <para>
/// The instance is the point. It is the SINGLE reflection source of truth, which
/// is what lets the harness stay product-agnostic: previously a hardcoded
/// name-to-Type switch inside the registry had to name all ten Family Hub plugin
/// types, so the "generic" core imported the product. Reflecting over the
/// registered instance's own type needs no such list, and the product pack
/// becomes the only place that knows which plugins exist.
/// </para>
///
/// <para>
/// ORDER IS SIGNIFICANT. The descriptor list drives kernel plugin registration
/// and (from the next commit) the planner's tool set, so it is the order the LLM
/// sees. It must stay as the product pack declares it.
/// </para>
/// </summary>
public sealed record CapabilityDescriptor
{
    /// <summary>The advertised module name — "Inventory", "ShoppingList", … .</summary>
    public required string Name { get; init; }

    /// <summary>The live plugin instance; the only thing reflection reads.</summary>
    public required object Instance { get; init; }

    public static CapabilityDescriptor From(string name, object instance)
        => new() { Name = name, Instance = instance };
}
