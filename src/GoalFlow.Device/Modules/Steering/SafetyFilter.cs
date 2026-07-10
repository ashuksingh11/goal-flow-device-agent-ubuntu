using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Steering;

/// <summary>
/// HARNESS MODULE: Safety &amp; Policy Filter — "LLM plans, code checks."
/// A Semantic Kernel <see cref="IFunctionInvocationFilter"/> that sits in the
/// kernel's invocation pipeline: EVERY function call the LLM makes via auto
/// function-calling passes through <see cref="OnFunctionInvocationAsync"/>
/// BEFORE the plugin method runs. The filter inspects the pending call
/// (context.Function + context.Arguments) against the dispatch's
/// <c>constraints.hard</c> — its ONLY input — and BLOCKS violations
/// deterministically. It never consults the LLM.
/// <para>
/// Blocking = do NOT call <c>next</c>; instead set <c>context.Result</c> to a
/// structured refusal (so the model sees WHY and can re-plan) and record the
/// violation for the plan_ready <c>safety</c> verdict.
/// </para>
/// </summary>
public sealed class SafetyFilter : IFunctionInvocationFilter
{
    private readonly ILogger<SafetyFilter> _logger;
    private readonly List<string> _violations = [];
    private JsonObject? _hardConstraints;

    public SafetyFilter(ILogger<SafetyFilter> logger) => _logger = logger;

    /// <summary>
    /// Arms the filter for a goal run. <paramref name="hardConstraints"/> is
    /// the dispatch's free-form <c>constraints.hard</c> object (allergens,
    /// medical, dietary, budget_cap, quiet_hours, ...).
    /// </summary>
    public void SetPolicy(JsonObject hardConstraints)
    {
        _hardConstraints = hardConstraints;
        _violations.Clear();
    }

    /// <summary>Violations recorded during the run → plan_ready payload.safety.</summary>
    public IReadOnlyList<string> Violations => _violations;

    /// <summary>Overall gate for the run ("passed" / "blocked").</summary>
    public string Gate => _violations.Count == 0 ? SafetyGates.Passed : SafetyGates.Blocked;

    /// <inheritdoc />
    public Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        // TODO(M1):
        //   1. var violation = Check(context.Function.PluginName, context.Function.Name, context.Arguments);
        //   2. if violation != null:
        //        _violations.Add(violation);
        //        _logger.LogWarning("safety_blocked {Module}.{Function}: {Violation}", ...);
        //        context.Result = new FunctionResult(context.Function,
        //            $"BLOCKED by safety policy: {violation}. Re-plan without violating hard constraints.");
        //        return Task.CompletedTask;       // <- veto: plugin method never runs
        //   3. return next(context);             // <- allowed: function executes
        throw new NotImplementedException("v2-M0 design skeleton");
    }

    /// <summary>
    /// Pure, deterministic check of one pending call against the armed policy.
    /// Returns a human-readable violation, or null when the call is allowed.
    /// </summary>
    internal string? Check(string? module, string function, KernelArguments arguments)
    {
        // TODO(M1): dispatch on the hard-constraint keys present in the policy:
        //   - allergens/dietary/medical -> CheckIngredients (recipe/list args must not contain them)
        //   - budget_cap                -> CheckBudgetCap   (ShoppingList.PlaceOrder estimated total)
        //   - quiet_hours               -> CheckQuietHours  (Appliance/Notify scheduled times)
        throw new NotImplementedException("v2-M0 design skeleton");
    }

    /// <summary>Allergen/dietary/medical screen over ingredient-bearing arguments.</summary>
    internal string? CheckIngredients(JsonObject policy, string function, KernelArguments arguments)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Blocks money-spending calls whose estimated total exceeds <c>budget_cap</c>.</summary>
    internal string? CheckBudgetCap(JsonObject policy, string function, KernelArguments arguments)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Blocks appliance/announce actions scheduled inside <c>quiet_hours</c>.</summary>
    internal string? CheckQuietHours(JsonObject policy, string function, KernelArguments arguments)
        => throw new NotImplementedException("v2-M0 design skeleton");
}
