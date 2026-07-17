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
    private readonly List<string> _violations = [];
    private JsonObject? _hardConstraints;
    private Trace? _trace;

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

    public void SetTrace(Trace trace) => _trace = trace;

    /// <summary>Violations recorded during the run → plan_ready payload.safety.</summary>
    public IReadOnlyList<string> Violations => _violations;

    /// <summary>Overall gate for the run ("passed" / "blocked").</summary>
    public string Gate => _violations.Count == 0 ? SafetyGates.Passed : SafetyGates.Blocked;

    /// <inheritdoc />
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var module = context.Function.PluginName ?? "";
        var function = context.Function.Name;
        await (_trace?.ToolCallAsync(module, function, ArgumentsToJson(context.Arguments)) ?? Task.CompletedTask);

        var violation = Check(module, function, context.Arguments);
        if (violation is not null)
        {
            _violations.Add(violation);
            _logger.LogWarning("safety_blocked {Module}.{Function}: {Violation}", module, function, violation);
            var refusal = $"BLOCKED by safety policy: {violation}. Re-plan without violating hard constraints.";
            context.Result = new FunctionResult(context.Function, refusal);
            await (_trace?.ToolResultAsync(module, function, refusal) ?? Task.CompletedTask);
            return;
        }

        await next(context);
        var resultText = context.Result.ToString();
        var resultViolation = CheckResult(module, function, resultText);
        if (resultViolation is not null)
        {
            _violations.Add(resultViolation);
            _logger.LogWarning("safety_result_blocked {Module}.{Function}: {Violation}", module, function, resultViolation);
            resultText = $"BLOCKED by safety policy: {resultViolation}. Re-plan without violating hard constraints.";
            context.Result = new FunctionResult(context.Function, resultText);
        }

        await (_trace?.ToolResultAsync(module, function, resultText) ?? Task.CompletedTask);
    }

    /// <summary>
    /// Pure, deterministic check of one pending call against the armed policy.
    /// Returns a human-readable violation, or null when the call is allowed.
    /// </summary>
    internal string? Check(string? module, string function, KernelArguments arguments)
    {
        if (_hardConstraints is null)
        {
            return null;
        }

        // PRODUCT-DEBT(M1): which check applies to which call is decided here by
        // hardcoded product module/function names. M1 turns these into declarative
        // rule bindings in the product pack's policy.json — the engine keeps the
        // check implementations (ported 1:1) and learns WHERE to apply them from
        // the pack. The ingredient-group table in ExpandIngredientGroups is the
        // same debt.
        return CheckIngredients(_hardConstraints, function, arguments)
            ?? (string.Equals(module, "ShoppingList", StringComparison.OrdinalIgnoreCase) ? CheckBudgetCap(_hardConstraints, function, arguments) : null)
            ?? ((string.Equals(module, "Appliance", StringComparison.OrdinalIgnoreCase) || string.Equals(module, "Notify", StringComparison.OrdinalIgnoreCase))
                ? CheckQuietHours(_hardConstraints, function, arguments)
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

    private string? CheckResult(string? module, string function, string resultText)
    {
        if (!string.Equals(module, "Recipes", StringComparison.OrdinalIgnoreCase) || _hardConstraints is null)
        {
            return null;
        }

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddStrings(_hardConstraints, "allergens", blocked);
        AddStrings(_hardConstraints, "dietary", blocked);
        AddStrings(_hardConstraints, "medical", blocked);
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

    private static IEnumerable<string> ArgumentStrings(KernelArguments args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return [];
        }

        if (value is string[] values)
        {
            return values;
        }

        if (value is IEnumerable<string> enumerable)
        {
            return enumerable;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        }

        var text = value.ToString() ?? "";
        return text.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('"'));
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
