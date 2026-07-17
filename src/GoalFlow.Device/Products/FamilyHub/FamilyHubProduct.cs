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
    private static string PolicyPath => ConfigPath("policy.json");

    /// <summary>Which runtime conditions this product's calls need (harness component 3).</summary>
    private static string PrechecksPath => ConfigPath("prechecks.json");

    /// <summary>
    /// Pack config resolves against the APP directory, not the working directory:
    /// it ships with the binary (a Content item in the csproj), so it is found
    /// wherever the agent is launched from, and on Tizen it lands in the read-only
    /// resource bundle — which is right for config and wrong for data/.
    /// </summary>
    private static string ConfigPath(string file)
        => Path.Combine(AppContext.BaseDirectory, "Products", "FamilyHub", "config", file);

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

        // Domain observers: what this product watches once a plan is live, and
        // which of its changes are worth waking the family for. The harness owns
        // the guarantee (material only, exactly once, one scoped re-plan); the
        // judgement is product knowledge and lives here.
        services.AddSingleton<IDomainObserver, MealPlanObserver>();
        services.AddSingleton<IDomainObserver, GuestDinnerObserver>();

        // Pre-checks: which of this product's runtime conditions matter, and how to
        // ask. Only the Family Hub knows it has an oven and a Samsung account.
        services.AddSingleton(_ => PrecheckBindings.Load(PrechecksPath));
        services.AddSingleton<IPrecheckProbe>(sp => new ApplianceOnlineProbe(sp.GetRequiredService<IProductApiAdapter>()));
        AddFlagProbe(services, "samsung_account", "samsung_account", "you are signed out of your Samsung account — sign in and this will resume");
        AddFlagProbe(services, "device_online", "device_online", "this Family Hub is offline");
        AddFlagProbe(services, "internet", "internet", "there is no internet connection");
        AddFlagProbe(services, "smartthings_connected", "smartthings_connected", "SmartThings is disconnected — the appliances can't be reached");
        AddFlagProbe(services, "payment_configured", "payment_configured", "no payment method is set up, so orders can't be placed");
        AddFlagProbe(services, "camera_operational", "camera_operational", "the interior camera isn't responding");
        AddFlagProbe(services, "ai_vision_initialized", "ai_vision_initialized", "AI Vision hasn't finished starting up");

        return services;
    }

    /// <summary>One flag in device_state.json, with the sentence a person can act on.</summary>
    private static void AddFlagProbe(IServiceCollection services, string id, string flag, string remediation)
        => services.AddSingleton<IPrecheckProbe>(sp =>
            new DeviceStateProbe(sp.GetRequiredService<IProductApiAdapter>(), id, flag, remediation));

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
