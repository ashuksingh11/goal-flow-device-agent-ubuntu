using GoalFlow.Device.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// THE FAMILY HUB PRODUCT PACK — the one place that knows which capabilities
/// this product has.
///
/// <para>
/// The plugin catalog used to be hand-listed in THREE places that could silently
/// disagree: the DI block in Program, the AddFromObject calls in
/// GoalAgent.BuildKernel, and the name-to-Type switch in the registry (plus a
/// fourth shadow copy in the planner's read-only whitelist). Adding a plugin
/// meant remembering all four. Now it is declared once, here, and the harness
/// reads it: Program registers this, BuildKernel loops the descriptors, and the
/// Capability Manager reflects over the same live instances.
/// </para>
///
/// <para>
/// This is what "generic harness, first product pack" means concretely: the
/// world stays MOCKED (these are the same SK plugins as always, backed by
/// data/*.json through <see cref="IProductApiAdapter"/>) — what changed is that
/// nothing in Harness/ names any of it.
/// </para>
/// </summary>
public static class FamilyHubProduct
{
    /// <summary>
    /// The pack's safety policy, resolved against the APP directory, not the
    /// working directory — it ships with the binary (see the Content item in the
    /// csproj), so it is found no matter where the agent is launched from, and on
    /// Tizen it resolves inside the read-only resource bundle.
    /// </summary>
    private static string PolicyPath =>
        Path.Combine(AppContext.BaseDirectory, "Products", "FamilyHub", "config", "policy.json");

    /// <summary>
    /// Registers the pack: the mock world adapter, the capability plugins, and
    /// the Capability Manager built over them. Requires <see cref="IClock"/> to
    /// be registered already (the adapter resolves day offsets against it).
    /// </summary>
    public static IServiceCollection AddFamilyHub(this IServiceCollection services, string dataDir)
    {
        services.AddSingleton<IProductApiAdapter>(sp => new MockFamilyHubAdapter(dataDir, sp.GetRequiredService<IClock>()));

        // This product's safety policy: which harness rule kinds apply to which of
        // its calls, and its ingredient vocabulary. Read-only and code-adjacent, so
        // it ships next to the pack rather than in the runtime --data dir.
        services.AddSingleton(_ => SafetyPolicy.Load(PolicyPath));

        services.AddSingleton<InventoryPlugin>();
        services.AddSingleton<CalendarPlugin>();
        services.AddSingleton<RecipePlugin>();
        services.AddSingleton<ShoppingListPlugin>();
        services.AddSingleton<ReminderPlugin>();
        services.AddSingleton<GuestsPlugin>();
        services.AddSingleton<ApplianceControlPlugin>();
        services.AddSingleton<FamilyProfilesPlugin>();
        services.AddSingleton<BudgetPlugin>();
        services.AddSingleton<NotifyPlugin>();

        services.AddSingleton(sp => new CapabilityManager(CreateDescriptors(sp), sp.GetRequiredService<SafetyPolicy>()));
        return services;
    }

    /// <summary>
    /// The capability catalog: advertised module name → live plugin instance.
    ///
    /// ORDER IS SIGNIFICANT AND MUST NOT BE CHANGED CASUALLY. It drives kernel
    /// registration and the planner's tool set, so it is the order the LLM sees
    /// its tools in — reordering is a behavior change, not a style choice.
    /// </summary>
    public static IReadOnlyList<CapabilityDescriptor> CreateDescriptors(IServiceProvider sp) =>
    [
        CapabilityDescriptor.From("Inventory",      sp.GetRequiredService<InventoryPlugin>()),
        CapabilityDescriptor.From("Calendar",       sp.GetRequiredService<CalendarPlugin>()),
        CapabilityDescriptor.From("Recipes",        sp.GetRequiredService<RecipePlugin>()),
        CapabilityDescriptor.From("ShoppingList",   sp.GetRequiredService<ShoppingListPlugin>()),
        CapabilityDescriptor.From("Reminders",      sp.GetRequiredService<ReminderPlugin>()),
        CapabilityDescriptor.From("Guests",         sp.GetRequiredService<GuestsPlugin>()),
        CapabilityDescriptor.From("Appliance",      sp.GetRequiredService<ApplianceControlPlugin>()),
        CapabilityDescriptor.From("FamilyProfiles", sp.GetRequiredService<FamilyProfilesPlugin>()),
        CapabilityDescriptor.From("Budget",         sp.GetRequiredService<BudgetPlugin>()),
        CapabilityDescriptor.From("Notify",         sp.GetRequiredService<NotifyPlugin>()),
    ];
}
