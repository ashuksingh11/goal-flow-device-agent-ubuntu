using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace GoalFlow.Device.Harness;

/// <summary>
/// WHICH CONSTRAINTS ARE IN FORCE, FOR WHICH GOAL — the armed policies and the
/// ambient goal scope. Held by <see cref="SafetyFilter"/>, which enforces them, and
/// read (never written) by anything that needs to REPORT a policy value.
///
/// <para>
/// WHY IT IS ITS OWN CLASS (v6). This state used to live inside the filter, which was
/// fine until a capability plugin needed to read it: Budget must tell the planner the
/// ceiling the goal will be enforced against. Injecting the filter into a plugin
/// closes a dependency cycle — SafetyFilter needs CapabilityManager (for the AX
/// check), CapabilityManager builds the plugin catalog, and the plugin would need the
/// filter — which the DI container resolves by deadlocking at startup, silently, with
/// no output. Splitting the STORE from the ENFORCER breaks it at the design level
/// instead of hiding it behind a lazy handle: this class depends on nothing.
/// </para>
///
/// <para>
/// PER GOAL, and that is load-bearing. It was once two plain fields on a singleton,
/// so with two goals in flight — which Program has always allowed, dispatching each
/// frame on its own Task.Run — goal B's dispatch overwrote goal A's constraints
/// mid-plan and the gate enforced the wrong family's allergens. Silent, in the exact
/// component whose whole purpose is to be trustworthy.
/// </para>
/// </summary>
public sealed class ArmedPolicies : IActivePolicy
{
    private readonly ConcurrentDictionary<string, GoalPolicy> _policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Which goal the current call belongs to. AsyncLocal because the kernel invokes
    /// plugin functions deep inside the planning await-chain: there is no parameter to
    /// thread a goal id through, but the ExecutionContext flows — including across the
    /// <c>Task.Run</c> that Program uses to dispatch frames.
    /// </summary>
    private static readonly AsyncLocal<string?> CurrentGoalId = new();

    /// <summary>One goal's armed constraints plus the violations recorded against it.</summary>
    public sealed class GoalPolicy
    {
        /// <summary>
        /// What the CLOUD sent, kept verbatim. Re-resolution starts here every time —
        /// never from the last effective block, or the envelope would be subtracted
        /// again on each pass and the ceiling would walk itself down to zero.
        ///
        /// <para>
        /// Settable only through <see cref="ReDispatch"/> (v7), and only when the account
        /// sends a new block. The device does not author policy, so nothing else writes here.
        /// </para>
        /// </summary>
        public required JsonObject Dispatched { get; set; }

        /// <summary>
        /// What is actually enforced: the dispatched block after the product's
        /// <see cref="IPolicyResolver"/> narrowed it against the world. Equal to
        /// <see cref="Dispatched"/> when a product declares no resolver.
        /// </summary>
        public required JsonObject Hard { get; set; }

        public List<string> Violations { get; } = [];
    }

    /// <summary>The goal this async flow belongs to, or null outside any scope.</summary>
    public string? CurrentGoal => CurrentGoalId.Value;

    /// <summary>Arms a goal's constraints and enters its scope; dispose leaves the scope only.</summary>
    /// <param name="effective">
    /// The block to enforce, when a resolver has already narrowed it. Omit and the
    /// dispatched block is enforced as sent.
    /// </param>
    public IDisposable Arm(string goalId, JsonObject hard, JsonObject? effective = null)
    {
        _policies[goalId] = new GoalPolicy { Dispatched = hard, Hard = effective ?? hard };
        return new GoalScope(goalId);
    }

    /// <summary>The dispatched (un-narrowed) constraints of an armed goal, for re-resolution.</summary>
    public JsonObject? DispatchedFor(string goalId)
        => _policies.TryGetValue(goalId, out var policy) ? policy.Dispatched : null;

    /// <summary>
    /// Replaces what is enforced for a goal, leaving the dispatched block and the
    /// recorded violations alone. Used when the world moved under an armed goal —
    /// another goal spent the shared budget — so the ceiling has to be recomputed.
    /// </summary>
    public void ReArm(string goalId, JsonObject effective)
    {
        if (_policies.TryGetValue(goalId, out var policy))
        {
            policy.Hard = effective;
        }
    }

    /// <summary>
    /// Replaces the DISPATCHED block itself — the account re-resolved this goal's
    /// constraints while it was running (v7).
    ///
    /// <para>
    /// Distinct from <see cref="ReArm"/>, and the distinction is the one that took a bug
    /// to learn: ReArm recomputes what is ENFORCED from an unchanged dispatched block, so
    /// narrowing can be undone when the world frees up. This replaces the dispatched
    /// block, which is only ever correct when the ACCOUNT sent a new one — the device must
    /// still never author policy. Violations are kept: they happened, and a new constraint
    /// arriving does not unmake them.
    /// </para>
    /// </summary>
    public void ReDispatch(string goalId, JsonObject hard, JsonObject? effective = null)
    {
        if (_policies.TryGetValue(goalId, out var policy))
        {
            policy.Dispatched = hard;
            policy.Hard = effective ?? hard;
        }
    }

    /// <summary>Re-enters an already-armed goal's scope (approvals, control ticks).</summary>
    public IDisposable Enter(string goalId) => new GoalScope(goalId);

    /// <summary>Forgets a goal's policy and violations (control: reset).</summary>
    public void Remove(string goalId) => _policies.TryRemove(goalId, out _);

    /// <summary>The armed policy of the ambient goal, or null when there is none.</summary>
    public GoalPolicy? Current()
        => CurrentGoalId.Value is { } goalId && _policies.TryGetValue(goalId, out var policy) ? policy : null;

    /// <summary>Violations recorded for one goal → its plan_ready payload.safety.</summary>
    public IReadOnlyList<string> ViolationsFor(string goalId)
        => _policies.TryGetValue(goalId, out var policy) ? policy.Violations.ToArray() : [];

    /// <inheritdoc />
    /// <remarks>A DEEP COPY: a reader may look at the policy, never edit it.</remarks>
    public JsonObject? ActiveHard() => Current() is { } policy ? (JsonObject)policy.Hard.DeepClone() : null;

    /// <summary>Sets the ambient goal for this async flow; restores the previous on dispose.</summary>
    private sealed class GoalScope : IDisposable
    {
        private readonly string? _previous;

        public GoalScope(string goalId)
        {
            _previous = CurrentGoalId.Value;
            CurrentGoalId.Value = goalId;
        }

        public void Dispose() => CurrentGoalId.Value = _previous;
    }
}
