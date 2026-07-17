using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Harness;

/// <summary>
/// HARNESS COMPONENT 2: the Safety Policy Engine's loaded policy — grade
/// overrides plus the deterministic rules bound to this product's calls.
///
/// <para>
/// Loaded from the product pack's <c>config/policy.json</c>. The engine
/// implements rule KINDS; the pack declares which calls they apply to. That is
/// the whole point: before v3 the "which check applies where" decisions were
/// hardcoded module-name comparisons inside the filter, so the generic core knew
/// this product's vocabulary.
/// </para>
///
/// <para>
/// The rules themselves are 1:1 ports of the v2 checks and read
/// <c>constraints.hard</c> and nothing else — deliberately. (One improvement,
/// approved and deliberate: term matching is token/stem-based, so
/// <c>allergens:["peanuts"]</c> now blocks "peanut butter", which it did not.)
/// </para>
/// </summary>
public sealed class SafetyPolicy
{
    private readonly IReadOnlyDictionary<string, AutomationGrade> _gradeOverrides;

    public IReadOnlyList<SafetyRule> Rules { get; }

    private SafetyPolicy(IReadOnlyDictionary<string, AutomationGrade> gradeOverrides, IReadOnlyList<SafetyRule> rules)
    {
        _gradeOverrides = gradeOverrides;
        Rules = rules;
    }

    /// <summary>A policy that blocks nothing — used when a product declares no config.</summary>
    public static SafetyPolicy Empty { get; } =
        new(new Dictionary<string, AutomationGrade>(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>
    /// THE SAFETY RATCHET. A config override may only make a grade STRICTER than
    /// the one the code declares via <see cref="SideEffectAttribute"/>; loosening
    /// throws at load.
    ///
    /// <para>
    /// Config and code can disagree, and the question is which way the disagreement
    /// is allowed to resolve. Tightening is a policy decision ("for THIS product,
    /// adding to the shopping list needs approval"). Loosening silently weakens the
    /// gate — the exact failure a policy file makes easy: one typo downgrading
    /// PlaceOrder from A2 to A0 and money moves without consent, with nothing to
    /// notice it. So the ratchet only turns one way, and a violation is fatal at
    /// startup rather than a warning nobody reads.
    /// </para>
    /// </summary>
    public AutomationGrade? GradeFor(string module, string function, AutomationGrade? intrinsic)
    {
        if (!_gradeOverrides.TryGetValue($"{module}.{function}", out var configured))
        {
            return intrinsic;
        }

        if (intrinsic is not null && configured < intrinsic)
        {
            throw new InvalidOperationException(
                $"policy.json weakens {module}.{function} from {intrinsic} to {configured}. " +
                "A policy file may only make a grade stricter, never looser — change the [SideEffect] attribute if that is really intended.");
        }

        return configured;
    }

    /// <summary>The violation from the first rule that objects, or null.</summary>
    public string? Evaluate(RuleStage stage, JsonObject hard, string? module, string function, KernelArguments arguments, string? resultText)
    {
        foreach (var rule in Rules)
        {
            if (rule.Stage != stage || !rule.AppliesTo(module, function))
            {
                continue;
            }

            var violation = rule.Evaluate(hard, function, arguments, resultText);
            if (violation is not null)
            {
                return violation;
            }
        }

        return null;
    }

    /// <summary>
    /// Loads a product pack's policy. Missing file = <see cref="Empty"/> (a
    /// product need not declare one); a malformed file THROWS rather than
    /// degrading to "no rules" — a safety config that silently half-loads is
    /// worse than one that refuses to start.
    /// </summary>
    public static SafetyPolicy Load(string path)
        => File.Exists(path)
            ? Parse(JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                    ?? throw new InvalidOperationException($"{path} is not a JSON object."), path)
            : Empty;

    /// <summary>Parses an already-read policy document. <paramref name="path"/> only labels errors.</summary>
    public static SafetyPolicy Parse(JsonObject root, string path)
    {
        var overrides = new Dictionary<string, AutomationGrade>(StringComparer.OrdinalIgnoreCase);
        if (root["grades"]?["overrides"] is JsonObject gradeNode)
        {
            foreach (var (key, value) in gradeNode)
            {
                overrides[key] = Grades.Parse(value?.GetValue<string>() ?? "", $"{path} grades.overrides['{key}']");
            }
        }

        var rules = new List<SafetyRule>();
        foreach (var node in root["rules"]?.AsArray() ?? [])
        {
            rules.Add(ParseRule(node?.AsObject() ?? throw new InvalidOperationException($"{path}: a rule is not an object."), path));
        }

        return new SafetyPolicy(overrides, rules);
    }

    private static SafetyRule ParseRule(JsonObject node, string path)
    {
        var kind = node["kind"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"{path}: a rule has no 'kind'.");
        var modules = Strings(node, "modules");
        var functions = Strings(node, "functions");

        return kind switch
        {
            "blocked_terms" or "result_screen" => new BlockedTermsRule(kind == "result_screen" ? RuleStage.Result : RuleStage.Arguments)
            {
                Modules = modules,
                Functions = functions,
                Constraints = Strings(node, "constraints"),
                ScanArgs = Strings(node, "scan_args"),
                Groups = Groups(node)
            },
            "numeric_cap" => new NumericCapRule
            {
                Modules = modules,
                Functions = functions,
                Constraint = Required(node, "constraint", path),
                Args = Strings(node, "args")
            },
            "time_window_block" => new TimeWindowBlockRule
            {
                Modules = modules,
                Functions = functions,
                Constraint = Required(node, "constraint", path),
                Args = Strings(node, "args")
            },
            _ => throw new InvalidOperationException(
                $"{path}: unknown rule kind '{kind}'. The engine implements the kinds; a pack may only instantiate them.")
        };
    }

    private static IReadOnlyList<string> Strings(JsonObject node, string key)
        => node[key] is JsonArray arr
            ? arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray()
            : [];

    private static string Required(JsonObject node, string key, string path)
        => node[key]?.GetValue<string>()
           ?? throw new InvalidOperationException($"{path}: rule '{node["kind"]}' requires '{key}'.");

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Groups(JsonObject node)
    {
        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (node["groups"] is JsonObject obj)
        {
            foreach (var (name, members) in obj)
            {
                groups[name] = members is JsonArray arr
                    ? arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray()
                    : [];
            }
        }

        return groups;
    }
}
