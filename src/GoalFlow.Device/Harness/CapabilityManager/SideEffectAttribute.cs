using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harness;

/// <summary>
/// Marks a [KernelFunction] as SIDE-EFFECTING with its approval tier.
/// Read by <see cref="CapabilityManager"/> for the capabilities advertisement
/// and by the SafetyFilter/ApprovalCoordinator to decide whether a pending call
/// must be frozen into a proposal instead of executing directly.
///
/// <para>
/// This is the INTRINSIC risk of a function, declared where the function lives:
/// a method that spends money says so in the same file that spends it. Config
/// (arriving with the policy engine) may later make a grade stricter, never
/// looser -- so the attribute and the config can't drift into an unsafe state.
/// </para>
///
/// <para>See <see cref="ApprovalTiers"/> (auto / light / firm).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SideEffectAttribute : Attribute
{
    public SideEffectAttribute(string tier) => Tier = tier;

    /// <summary>See <see cref="ApprovalTiers"/> (auto / light / firm).</summary>
    public string Tier { get; }
}
