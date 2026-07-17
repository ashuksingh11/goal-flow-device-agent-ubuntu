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

    public SafetyFilter(ILogger<SafetyFilter> logger) => _logger = logger;

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
    /// </summary>
    internal string? Check(JsonObject? hard, string? module, string function, KernelArguments arguments)
    {
        if (hard is null)
        {
            return null;
        }

        // PRODUCT-DEBT(M1): which check applies to which call is decided here by
        // hardcoded product module/function names. M1 turns these into declarative
        // rule bindings in the product pack's policy.json — the engine keeps the
        // check implementations (ported 1:1) and learns WHERE to apply them from
        // the pack. The ingredient-group table in ExpandIngredientGroups is the
        // same debt.
        return CheckIngredients(hard, function, arguments)
            ?? (string.Equals(module, "ShoppingList", StringComparison.OrdinalIgnoreCase) ? CheckBudgetCap(hard, function, arguments) : null)
            ?? ((string.Equals(module, "Appliance", StringComparison.OrdinalIgnoreCase) || string.Equals(module, "Notify", StringComparison.OrdinalIgnoreCase))
                ? CheckQuietHours(hard, function, arguments)
                : null);
    }

    /// <summary>Allergen/dietary/medical screen over ingredient-bearing arguments.</summary>
    internal string? CheckIngredients(JsonObject policy, string function, KernelArguments arguments)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddStrings(policy, "allergens", blocked);
        AddStrings(policy, "dietary", blocked);
        AddStrings(policy, "medical", blocked);
        ExpandIngredientGroups(blocked);
        if (blocked.Count == 0)
        {
            return null;
        }

        var scan = new JsonObject();
        foreach (var key in new[] { "items", "ingredients", "name", "item", "message", "title" })
        {
            if (arguments.TryGetValue(key, out var value))
            {
                scan[key] = JsonSerializer.SerializeToNode(value, ContractJson.Options);
            }
        }

        var haystack = scan.ToJsonString(ContractJson.Options);
        foreach (var term in blocked)
        {
            var normalized = NormalizeDietary(term);
            if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                haystack.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return $"{function} arguments include hard-blocked ingredient or group '{term}'";
            }
        }

        return null;
    }

    private static string? CheckResult(JsonObject? hard, string? module, string function, string resultText)
    {
        if (!string.Equals(module, "Recipes", StringComparison.OrdinalIgnoreCase) || hard is null)
        {
            return null;
        }

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddStrings(hard, "allergens", blocked);
        AddStrings(hard, "dietary", blocked);
        AddStrings(hard, "medical", blocked);
        ExpandIngredientGroups(blocked);
        foreach (var term in blocked)
        {
            var normalized = NormalizeDietary(term);
            if (resultText.Contains($"\"{term}\"", StringComparison.OrdinalIgnoreCase) ||
                resultText.Contains($"\"{normalized}\"", StringComparison.OrdinalIgnoreCase))
            {
                return $"{function} result contains hard-blocked ingredient or group '{term}'";
            }
        }

        return null;
    }

    /// <summary>Blocks money-spending calls whose estimated total exceeds <c>budget_cap</c>.</summary>
    internal string? CheckBudgetCap(JsonObject policy, string function, KernelArguments arguments)
    {
        if (!string.Equals(function, "PlaceOrder", StringComparison.OrdinalIgnoreCase) ||
            policy["budget_cap"] is null ||
            policy["budget_cap"]!.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        var cap = policy["budget_cap"]!.GetValue<double>();
        var total = GetDouble(arguments, "estimatedTotal") ?? GetDouble(arguments, "estimated_total") ?? 0;
        return total > cap ? $"PlaceOrder estimate {total:0.00} exceeds budget_cap {cap:0.00}" : null;
    }

    /// <summary>Blocks appliance/announce actions scheduled inside <c>quiet_hours</c>.</summary>
    internal string? CheckQuietHours(JsonObject policy, string function, KernelArguments arguments)
    {
        if (policy["quiet_hours"] is not JsonObject quiet)
        {
            return null;
        }

        var timeText = GetString(arguments, "time") ?? ExtractTime(GetString(arguments, "atTime"));
        if (timeText is null)
        {
            return null;
        }

        var startText = quiet["start"]?.GetValue<string>();
        var endText = quiet["end"]?.GetValue<string>();
        if (startText is null || endText is null)
        {
            return null;
        }

        var time = TimeOnly.Parse(timeText);
        var start = TimeOnly.Parse(startText);
        var end = TimeOnly.Parse(endText);
        var inside = start <= end ? time >= start && time < end : time >= start || time < end;
        return inside ? $"{function} scheduled at {timeText} inside quiet_hours {startText}-{endText}" : null;
    }

    private static void AddStrings(JsonObject policy, string key, HashSet<string> target)
    {
        if (policy[key] is not JsonArray arr)
        {
            return;
        }

        foreach (var item in arr)
        {
            var value = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(value);
            }
        }
    }

    private static void ExpandIngredientGroups(HashSet<string> target)
    {
        if (target.Contains("dairy") || target.Contains("no_dairy"))
        {
            target.Add("milk");
            target.Add("yogurt");
            target.Add("paneer");
            target.Add("cheese");
        }

        if (target.Contains("gluten") || target.Contains("no_gluten"))
        {
            target.Add("bread");
            target.Add("toast");
            target.Add("pasta");
            target.Add("tortilla wraps");
        }

        if (target.Contains("pork") || target.Contains("no_pork"))
        {
            target.Add("pork");
            target.Add("bacon");
            target.Add("ham");
        }
    }

    private static double? GetDouble(KernelArguments args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetDouble(),
            _ when double.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? GetString(KernelArguments args, string name)
        => args.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static string? ExtractTime(string? atTime)
    {
        if (string.IsNullOrWhiteSpace(atTime))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(atTime, out var dto))
        {
            return TimeOnly.FromDateTime(dto.DateTime).ToString("HH:mm");
        }

        return TimeOnly.TryParse(atTime, out var time) ? time.ToString("HH:mm") : null;
    }


    private static string NormalizeDietary(string value)
        => value.StartsWith("no_", StringComparison.OrdinalIgnoreCase) ? value[3..] : value;

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
