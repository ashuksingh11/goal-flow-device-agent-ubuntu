using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.Json;
using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Harness;

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

    /// <summary>
    /// Armed policy + recorded violations, PER GOAL — see <see cref="ArmedPolicies"/>,
    /// which owns that state so a capability plugin can READ the armed policy without
    /// taking a reference to the enforcer (and closing a DI cycle).
    /// </summary>
    private readonly ArmedPolicies _armed;

    private Trace? _trace;

    private readonly SafetyPolicy _policy;
    private readonly CapabilityManager _capabilities;

    /// <summary>
    /// The product's policy resolution, if it declares one — how the world narrows a
    /// dispatched ceiling (v6-M3: the household envelope minus what is already spent).
    /// Null is the normal case for a product with no such policy: arm what was sent.
    /// </summary>
    private readonly IPolicyResolver? _resolver;

    public SafetyFilter(
        ILogger<SafetyFilter> logger,
        SafetyPolicy policy,
        CapabilityManager capabilities,
        ArmedPolicies armed,
        IPolicyResolver? resolver = null)
    {
        _logger = logger;
        _policy = policy;
        _capabilities = capabilities;
        _armed = armed;
        _resolver = resolver;
    }

    /// <summary>
    /// Arms the filter for a goal and enters its scope. <paramref name="hardConstraints"/>
    /// is the dispatch's free-form <c>constraints.hard</c> (allergens, medical,
    /// dietary, budget_cap, quiet_hours, …). Dispose leaves the scope; the policy
    /// STAYS armed, because approvals and control ticks arrive later and must be
    /// checked against their own goal's constraints (see <see cref="EnterGoal"/>).
    ///
    /// <para>
    /// WHY PER GOAL: this was a live safety bug. The policy was a single field
    /// set at the top of every plan run, so with two goals in flight — which
    /// Program has always allowed, dispatching each frame on its own Task.Run —
    /// goal B's dispatch overwrote goal A's hard constraints mid-plan, and the
    /// deterministic gate then enforced the wrong family's allergens against
    /// goal A's calls. Silent, and in the exact component whose entire purpose is
    /// to be trustworthy. Agent Board makes concurrent goals routine, so this had
    /// to be fixed before anything can submit a second goal.
    /// </para>
    /// </summary>
    public IDisposable BeginGoal(string goalId, JsonObject hardConstraints)
        => _armed.Arm(goalId, hardConstraints);

    /// <summary>
    /// Arms a goal AFTER letting the product narrow the dispatched constraints against
    /// the world (v6-M3). Prefer this on the planning path; <see cref="BeginGoal"/>
    /// remains for callers with nothing to resolve.
    /// </summary>
    public async Task<IDisposable> BeginGoalAsync(string goalId, JsonObject hardConstraints, CancellationToken ct = default)
    {
        var effective = await ResolveAsync(goalId, hardConstraints, ct);
        return _armed.Arm(goalId, hardConstraints, effective);
    }

    /// <summary>
    /// Recomputes an armed goal's ceiling from its DISPATCHED constraints and the world
    /// as it is now.
    ///
    /// <para>
    /// Called wherever the world may have moved under a goal that is already armed: at
    /// approval time (another goal may have spent the shared budget since this plan was
    /// made) and on each day tick.
    /// </para>
    ///
    /// <para>
    /// ALWAYS FROM THE DISPATCHED BLOCK, never from the last effective one. Narrowing
    /// is a min(), so re-narrowing an already-narrowed cap looks harmless — until the
    /// envelope FREES UP (a refund, a new billing period) and the ceiling cannot climb
    /// back, because min() only ever goes down. Recomputing from what the account
    /// actually said lets the ceiling recover as well as fall.
    /// </para>
    /// </summary>
    public async Task ReResolveAsync(string goalId, CancellationToken ct = default)
    {
        if (_resolver is null || _armed.DispatchedFor(goalId) is not { } dispatched)
        {
            return;
        }

        _armed.ReArm(goalId, await ResolveAsync(goalId, dispatched, ct));
    }

    private async Task<JsonObject> ResolveAsync(string goalId, JsonObject dispatched, CancellationToken ct)
    {
        if (_resolver is null)
        {
            return dispatched;
        }

        try
        {
            var effective = await _resolver.ResolveAsync(dispatched, ct);
            if (!JsonNode.DeepEquals(effective, dispatched))
            {
                _logger.LogInformation(
                    "policy_resolved goal={GoalId} dispatched_cap={Dispatched} effective_cap={Effective}",
                    goalId, dispatched["budget_cap"]?.ToJsonString(), effective["budget_cap"]?.ToJsonString());
            }

            return effective;
        }
        catch (Exception ex)
        {
            // Arm the DISPATCHED policy rather than nothing. A resolver that cannot read
            // the world has lost the tightening, not the constraint — falling back to no
            // policy at all would turn a bad read into an open gate.
            _logger.LogError(ex, "policy_resolve_failed goal={GoalId} — arming the dispatched constraints unnarrowed", goalId);
            return dispatched;
        }
    }

    /// <summary>
    /// Re-enters an already-armed goal's scope, for calls that happen after
    /// planning: actuating an approved proposal, or a control tick's adaptation.
    ///
    /// <para>
    /// The approval path in particular used to run with NO policy of its own —
    /// it never armed one, so it silently reused whatever the last plan run left
    /// behind. With one goal that is the same policy by luck; with two it means
    /// the last gate before a real side effect (spending money, starting an
    /// appliance) checks the wrong goal's constraints.
    /// </para>
    /// </summary>
    public IDisposable EnterGoal(string goalId) => _armed.Enter(goalId);

    /// <summary>
    /// The marker every refusal starts with. A CONSTANT, not a repeated literal:
    /// the approval path has to recognise a refused call to report it honestly
    /// (ExecutionResults.BlockedSafety), and a magic string copied into two files is
    /// how that check quietly stops matching.
    /// </summary>
    public const string RefusalPrefix = "BLOCKED by safety policy:";

    /// <summary>The refusal the model — and the approval path — sees.</summary>
    public static string Refusal(string violation)
        => $"{RefusalPrefix} {violation}. Re-plan without violating hard constraints.";

    /// <summary>True when a kernel result is one of our refusals rather than a real answer.</summary>
    public static bool IsRefusal(string? resultText)
        => resultText?.StartsWith(RefusalPrefix, StringComparison.Ordinal) == true;

    /// <summary>Forgets a goal's policy and violations (control: reset).</summary>
    public void RemoveGoal(string goalId) => _armed.Remove(goalId);

    public void SetTrace(Trace trace) => _trace = trace;

    /// <summary>Violations recorded for one goal → its plan_ready payload.safety.</summary>
    public IReadOnlyList<string> ViolationsFor(string goalId) => _armed.ViolationsFor(goalId);

    /// <summary>That goal's overall gate ("passed" / "blocked").</summary>
    public string GateFor(string goalId)
        => ViolationsFor(goalId).Count == 0 ? SafetyGates.Passed : SafetyGates.Blocked;

    /// <inheritdoc />
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var module = context.Function.PluginName ?? "";
        var function = context.Function.Name;
        await (_trace?.ToolCallAsync(module, function, ArgumentsToJson(context.Arguments)) ?? Task.CompletedTask);

        var policy = CurrentPolicy(module, function);
        var violation = Check(policy?.Hard, module, function, context.Arguments);
        if (violation is not null)
        {
            policy?.Violations.Add(violation);
            _logger.LogWarning("safety_blocked {Module}.{Function}: {Violation}", module, function, violation);
            var refusal = Refusal(violation);
            context.Result = new FunctionResult(context.Function, refusal);
            await (_trace?.ToolResultAsync(module, function, refusal) ?? Task.CompletedTask);
            return;
        }

        await next(context);
        var resultText = context.Result.ToString();
        var resultViolation = CheckResult(policy?.Hard, module, function, resultText);
        if (resultViolation is not null)
        {
            policy?.Violations.Add(resultViolation);
            _logger.LogWarning("safety_result_blocked {Module}.{Function}: {Violation}", module, function, resultViolation);
            resultText = Refusal(resultViolation);
            context.Result = new FunctionResult(context.Function, resultText);
        }

        await (_trace?.ToolResultAsync(module, function, resultText) ?? Task.CompletedTask);
    }

    /// <summary>
    /// The policy of the goal this call belongs to, or null if the flow is not
    /// inside a goal scope.
    ///
    /// <para>
    /// Null means "no constraints were declared for this call" — the same
    /// situation as a dispatch with an empty <c>constraints.hard</c>, so it is
    /// not a fail-open: there is genuinely nothing to enforce. But it is also
    /// how the old approval path silently ran, so it is logged loudly enough to
    /// be noticed if a code path ever forgets to enter a scope.
    /// </para>
    /// </summary>
    private ArmedPolicies.GoalPolicy? CurrentPolicy(string module, string function)
    {
        if (_armed.Current() is { } policy)
        {
            return policy;
        }

        _logger.LogWarning(
            "safety_unscoped {Module}.{Function} ran with no armed policy (goal_id={GoalId}) — nothing to enforce; a caller likely forgot BeginGoal/EnterGoal",
            module, function, _armed.CurrentGoal ?? "<none>");
        return null;
    }

    /// <summary>
    /// Resolves the ambient goal's constraints and checks one call against them.
    /// Exercises the same scope lookup the kernel pipeline uses, so tests of goal
    /// isolation go through the real path.
    /// </summary>
    internal string? CheckCurrent(string module, string function, KernelArguments arguments)
        => Check(CurrentPolicy(module, function)?.Hard, module, function, arguments);

    /// <summary>
    /// Pure, deterministic check of one pending call against one goal's hard
    /// constraints. Returns a human-readable violation, or null when allowed.
    ///
    /// <para>
    /// The decisions live in the product pack's policy.json now; this only asks
    /// the rules bound to this call. It used to be a chain of hardcoded
    /// module-name comparisons ("ShoppingList" ? check the budget cap), which is
    /// exactly the product knowledge that does not belong in the harness.
    /// </para>
    /// </summary>
    internal string? Check(JsonObject? hard, string? module, string function, KernelArguments arguments)
    {
        // AX FIRST, and independent of the goal's constraints. A prohibited action
        // is not "blocked because of what this family asked for" — it is blocked
        // because the agent does not do it, for anyone, ever. So it is checked
        // before the policy is even consulted, and it blocks even a call with no
        // constraints armed at all.
        if (_capabilities.GradeOf(module ?? "", function) == AutomationGrade.AX)
        {
            return $"{module}.{function} is prohibited (grade AX): this action is never performed automatically";
        }

        return hard is null ? null : _policy.Evaluate(RuleStage.Arguments, hard, module, function, arguments, null);
    }

    /// <summary>Screens what a function RETURNED, so a forbidden thing never reaches the model.</summary>
    private string? CheckResult(JsonObject? hard, string? module, string function, string resultText)
        => hard is null ? null : _policy.Evaluate(RuleStage.Result, hard, module, function, [], resultText);








    private static JsonObject ArgumentsToJson(KernelArguments args)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in args)
        {
            obj[key] = JsonSerializer.SerializeToNode(value, ContractJson.Options);
        }

        return obj;
    }
}
