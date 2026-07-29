using System.Text.Json;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Harness;

/// <summary>Where in a call's lifecycle a rule applies.</summary>
public enum RuleStage
{
    /// <summary>Before the plugin method runs, against its arguments.</summary>
    Arguments,

    /// <summary>After it runs, against what it returned (so a forbidden thing never reaches the model).</summary>
    Result
}

/// <summary>
/// One deterministic safety check, bound to the calls it applies to.
///
/// <para>
/// THE SPLIT THAT MATTERS: the harness implements rule KINDS (what a
/// blocked-terms check *is*); the product pack declares rule INSTANCES (that
/// this product's budget cap applies to ShoppingList.PlaceOrder, that its quiet
/// hours apply to Appliance and Notify). Before v3 both lived here as hardcoded
/// module-name comparisons, which is why the "generic" core knew what a shopping
/// list was.
/// </para>
///
/// <para>
/// Rules read <c>constraints.hard</c> and nothing else — that invariant is
/// unchanged and is what "LLM plans, code checks" rests on.
/// </para>
/// </summary>
public abstract class SafetyRule
{
    /// <summary>Modules this rule applies to; empty = all.</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];

    /// <summary>Functions this rule applies to; empty = all.</summary>
    public IReadOnlyList<string> Functions { get; init; } = [];

    public virtual RuleStage Stage => RuleStage.Arguments;

    /// <summary>True when this rule is bound to the given call.</summary>
    public bool AppliesTo(string? module, string function)
        => (Modules.Count == 0 || Modules.Contains(module ?? "", StringComparer.OrdinalIgnoreCase))
        && (Functions.Count == 0 || Functions.Contains(function, StringComparer.OrdinalIgnoreCase));

    /// <summary>The violation, or null when this rule is satisfied.</summary>
    public abstract string? Evaluate(JsonObject hard, string function, KernelArguments arguments, string? resultText);

    /// <summary>Reads a string array out of constraints.hard.</summary>
    protected static void AddStrings(JsonObject policy, string key, HashSet<string> target)
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

    /// <summary>"no_pork" and "pork" mean the same restriction.</summary>
    protected static string NormalizeDietary(string value)
        => value.StartsWith("no_", StringComparison.OrdinalIgnoreCase) ? value[3..] : value;
}

/// <summary>
/// Blocks calls whose text mentions something the family cannot have — the
/// allergen/dietary/medical screen. Applies to every module by default: an
/// ingredient is forbidden wherever it appears.
/// </summary>
public sealed class BlockedTermsRule : SafetyRule
{
    /// <summary>constraints.hard keys to union (allergens, dietary, medical).</summary>
    public required IReadOnlyList<string> Constraints { get; init; }

    /// <summary>Argument names worth scanning (items, ingredients, name, …).</summary>
    public required IReadOnlyList<string> ScanArgs { get; init; }

    /// <summary>
    /// Product vocabulary: one constraint word standing for concrete things
    /// ("dairy" → milk, yogurt, paneer, cheese). Product knowledge, so it lives
    /// in the pack's config, not in this class.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Groups { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    public override RuleStage Stage { get; } = RuleStage.Arguments;

    public BlockedTermsRule(RuleStage stage = RuleStage.Arguments) => Stage = stage;

    public override string? Evaluate(JsonObject hard, string function, KernelArguments arguments, string? resultText)
    {
        var blocked = BlockedTerms(hard);
        if (blocked.Count == 0)
        {
            return null;
        }

        var text = Stage == RuleStage.Result ? resultText ?? "" : ScannedText(arguments);
        foreach (var term in blocked)
        {
            if (TermMatcher.Matches(term, text))
            {
                var where = Stage == RuleStage.Result ? "result contains" : "arguments include";
                return $"{function} {where} hard-blocked ingredient or group '{term}'";
            }
        }

        return null;
    }

    private HashSet<string> BlockedTerms(JsonObject hard)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Constraints)
        {
            AddStrings(hard, key, blocked);
        }

        // A group name blocks the group name AND everything it stands for.
        foreach (var (group, members) in Groups)
        {
            if (blocked.Contains(group) || blocked.Contains($"no_{group}"))
            {
                foreach (var member in members)
                {
                    blocked.Add(member);
                }
            }
        }

        foreach (var term in blocked.ToArray())
        {
            blocked.Add(NormalizeDietary(term));
        }

        return blocked;
    }

    private string ScannedText(KernelArguments arguments)
    {
        var scan = new JsonObject();
        foreach (var key in ScanArgs)
        {
            if (arguments.TryGetValue(key, out var value))
            {
                scan[key] = JsonSerializer.SerializeToNode(value, ContractJson.Options);
            }
        }

        return scan.ToJsonString(ContractJson.Options);
    }
}

/// <summary>Blocks a call whose numeric argument exceeds a cap in constraints.hard (the budget guardrail).</summary>
public sealed class NumericCapRule : SafetyRule
{
    /// <summary>The constraints.hard key holding the cap (budget_cap).</summary>
    public required string Constraint { get; init; }

    /// <summary>Argument names that might carry the amount.</summary>
    public required IReadOnlyList<string> Args { get; init; }

    public override string? Evaluate(JsonObject hard, string function, KernelArguments arguments, string? resultText)
    {
        if (hard[Constraint] is null || hard[Constraint]!.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        var cap = hard[Constraint]!.GetValue<double>();
        var total = Args.Select(name => GetDouble(arguments, name)).FirstOrDefault(v => v is not null) ?? 0;
        return total > cap ? $"{function} estimate {total:0.00} exceeds {Constraint} {cap:0.00}" : null;
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
}

/// <summary>Blocks a call scheduled inside a forbidden time window (quiet hours).</summary>
public sealed class TimeWindowBlockRule : SafetyRule
{
    /// <summary>The constraints.hard key holding {start, end} (quiet_hours).</summary>
    public required string Constraint { get; init; }

    /// <summary>Argument names that might carry the time.</summary>
    public required IReadOnlyList<string> Args { get; init; }

    public override string? Evaluate(JsonObject hard, string function, KernelArguments arguments, string? resultText)
    {
        if (hard[Constraint] is not JsonObject window)
        {
            return null;
        }

        var timeText = Args.Select(name => ExtractTime(GetString(arguments, name))).FirstOrDefault(t => t is not null);
        var startText = window["start"]?.GetValue<string>();
        var endText = window["end"]?.GetValue<string>();
        if (timeText is null || startText is null || endText is null)
        {
            return null;
        }

        var time = TimeOnly.Parse(timeText);
        var start = TimeOnly.Parse(startText);
        var end = TimeOnly.Parse(endText);
        // The window usually wraps midnight (21:30–07:00), hence the two cases.
        var inside = start <= end ? time >= start && time < end : time >= start || time < end;
        return inside ? $"{function} scheduled at {timeText} inside {Constraint} {startText}-{endText}" : null;
    }

    private static string? GetString(KernelArguments args, string name)
        => args.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static string? ExtractTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, out var dto))
        {
            return TimeOnly.FromDateTime(dto.DateTime).ToString("HH:mm");
        }

        return TimeOnly.TryParse(value, out var time) ? time.ToString("HH:mm") : null;
    }
}

/// <summary>
/// Blocks a call scheduled on a DATE inside a forbidden date range — the away
/// window: nobody is home, so nothing may be set running in the house.
///
/// <para>
/// The sibling of <see cref="TimeWindowBlockRule"/>, and separate on purpose: that
/// one compares a time of day (and wraps midnight), this one compares a calendar
/// date. Folding them into one rule would mean one Evaluate guessing which kind of
/// value it was handed, which is how "22:00" ends up parsed as a date.
/// </para>
///
/// <para>
/// ENDPOINTS ARE EXCLUSIVE. The family is home for part of the departure day and
/// part of the return day, and the vacation plan's own best move — run the
/// dishwasher before you leave — happens ON the departure date. Blocking the travel
/// days would veto the plan the goal exists to produce, so only the days the house
/// is genuinely empty count. Whole-day granularity: the seeded world knows which
/// DAY the family leaves, not the hour.
/// </para>
/// </summary>
public sealed class DateWindowBlockRule : SafetyRule
{
    /// <summary>The constraints.hard key holding {start, end} as ISO dates (away_window).</summary>
    public required string Constraint { get; init; }

    /// <summary>Argument names that might carry the date ("atTime", "date", "when").</summary>
    public required IReadOnlyList<string> Args { get; init; }

    public override string? Evaluate(JsonObject hard, string function, KernelArguments arguments, string? resultText)
    {
        if (hard[Constraint] is not JsonObject window)
        {
            return null;
        }

        if (!DateOnly.TryParse(window["start"]?.GetValue<string>(), out var start)
            || !DateOnly.TryParse(window["end"]?.GetValue<string>(), out var end))
        {
            return null;
        }

        foreach (var name in Args)
        {
            var scheduled = ExtractDate(GetString(arguments, name));
            if (scheduled is { } date && date > start && date < end)
            {
                return $"{function} is scheduled for {date:yyyy-MM-dd}, inside {Constraint} {start:yyyy-MM-dd}-{end:yyyy-MM-dd} — nobody is home";
            }
        }

        return null;
    }

    private static string? GetString(KernelArguments args, string name)
        => args.TryGetValue(name, out var value) ? value?.ToString() : null;

    /// <summary>
    /// The ISO date prefix of an argument, or null when it carries no date.
    ///
    /// <para>
    /// Deliberately strict rather than DateTime.TryParse: that helpfully resolves a
    /// bare "22:00" to TODAY, which would make a quiet-hours-shaped argument look
    /// like a date inside the away window and block a call nobody scheduled while
    /// away. The contract's arg shapes are "YYYY-MM-DD" and "YYYY-MM-DDTHH:mm", so
    /// requiring that prefix costs nothing real and removes the false positive.
    /// </para>
    /// </summary>
    private static DateOnly? ExtractDate(string? value)
    {
        if (value is null || value.Length < 10)
        {
            return null;
        }

        return DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", out var date) ? date : null;
    }
}
