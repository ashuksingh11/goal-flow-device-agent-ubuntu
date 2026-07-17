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
    /// Armed policy + recorded violations, PER GOAL. This used to be two plain
    /// fields on a singleton, which made the gate unsound the moment two goals
    /// overlapped — see <see cref="BeginGoal"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, GoalPolicy> _policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Which goal the current call belongs to. AsyncLocal because the kernel
    /// invokes plugin functions deep inside the planning await-chain: there is no
    /// parameter to thread a goal id through, but the ExecutionContext flows —
    /// including across the <c>Task.Run</c> that Program uses to dispatch frames.
    /// </summary>
    private static readonly AsyncLocal<string?> CurrentGoalId = new();

    private Trace? _trace;

    private readonly SafetyPolicy _policy;

    public SafetyFilter(ILogger<SafetyFilter> logger, SafetyPolicy policy)
    {
        _logger = logger;
        _policy = policy;
    }

    private sealed class GoalPolicy
    {
        public required JsonObject Hard { get; init; }
        public List<string> Violations { get; } = [];
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
    {
        _policies[goalId] = new GoalPolicy { Hard = hardConstraints };
        return new GoalScope(goalId);
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
    public IDisposable EnterGoal(string goalId) => new GoalScope(goalId);

    /// <summary>Forgets a goal's policy and violations (control: reset).</summary>
    public void RemoveGoal(string goalId) => _policies.TryRemove(goalId, out _);

    public void SetTrace(Trace trace) => _trace = trace;

    /// <summary>Violations recorded for one goal → its plan_ready payload.safety.</summary>
    public IReadOnlyList<string> ViolationsFor(string goalId)
        => _policies.TryGetValue(goalId, out var policy) ? policy.Violations.ToArray() : [];

    /// <summary>That goal's overall gate ("passed" / "blocked").</summary>
    public string GateFor(string goalId)
        => ViolationsFor(goalId).Count == 0 ? SafetyGates.Passed : SafetyGates.Blocked;

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
            var refusal = $"BLOCKED by safety policy: {violation}. Re-plan without violating hard constraints.";
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
            resultText = $"BLOCKED by safety policy: {resultViolation}. Re-plan without violating hard constraints.";
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
    private GoalPolicy? CurrentPolicy(string module, string function)
    {
        var goalId = CurrentGoalId.Value;
        if (goalId is not null && _policies.TryGetValue(goalId, out var policy))
        {
            return policy;
        }

        _logger.LogWarning(
            "safety_unscoped {Module}.{Function} ran with no armed policy (goal_id={GoalId}) — nothing to enforce; a caller likely forgot BeginGoal/EnterGoal",
            module, function, goalId ?? "<none>");
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
        => hard is null ? null : _policy.Evaluate(RuleStage.Arguments, hard, module, function, arguments, null);

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
