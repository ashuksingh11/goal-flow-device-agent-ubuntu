using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Gate-phase harness #1 of 2 — the SAFETY gate.
/// <para>
/// Deterministic CODE (no LLM, no network, no randomness) that checks a
/// candidate plan against <c>constraints.hard</c> — its ONLY input besides
/// the plan and the recipe facts in the world state. On violation it BLOCKS
/// and reports <c>hard_violations</c>.
/// </para>
/// <para>
/// Kept as a separate class from the planner and from the approval gate by
/// design: the safety gate is code and blocks; the approval gate is the user
/// (via the cloud) and waits. NEVER let the planner run this check.
/// </para>
/// Build effort: FULL logic later. Synchronous on purpose — determinism.
/// </summary>
public interface ISafetyGate
{
    /// <summary>
    /// Checks every dish and proposal against the hard constraints
    /// (allergens, dietary, medical) using recipe <c>contains</c>/ingredient
    /// facts from <paramref name="world"/>. Returns gate="passed" with an
    /// empty violation list, or gate="blocked" with the violations.
    /// </summary>
    SafetyResult Check(CandidatePlan plan, HardConstraints hard, WorldState world);
}

/// <summary>Skeleton implementation — full logic in the implementation phase.</summary>
public sealed class SafetyGate : ISafetyGate
{
    private readonly ITrace _trace;
    private readonly IClock _clock;

    public SafetyGate(ITrace trace, IClock clock)
    {
        _trace = trace;
        _clock = clock;
    }

    public SafetyResult Check(CandidatePlan plan, HardConstraints hard, WorldState world)
    {
        var excludedTerms = hard.Allergens
            .Concat(hard.Dietary.SelectMany(ExpandDietaryRule))
            .Concat(hard.Medical)
            .Select(Normalize)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var violations = new List<string>();
        foreach (var item in plan.Plan)
        {
            var dish = Normalize(item.Dish);
            foreach (var term in excludedTerms)
            {
                if (ContainsTerm(dish, term))
                {
                    violations.Add($"{item.Day}:{item.Dish}:contains_{term}");
                }
            }
        }

        var result = new SafetyResult
        {
            Gate = violations.Count == 0 ? SafetyResult.GatePassed : SafetyResult.GateBlocked,
            HardViolations = violations,
        };

        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            Phase = TracePhase.Gate,
            Source = nameof(SafetyGate),
            Kind = "gate_outcome",
            Message = $"safety gate {result.Gate}",
            Data = new Dictionary<string, string>
            {
                ["hard_violations"] = violations.Count.ToString(),
            },
        });

        return result;
    }

    private static IEnumerable<string> ExpandDietaryRule(string rule)
    {
        yield return rule;

        if (rule.StartsWith("no_", StringComparison.OrdinalIgnoreCase))
        {
            yield return rule["no_".Length..];
        }

        if (rule.StartsWith("without_", StringComparison.OrdinalIgnoreCase))
        {
            yield return rule["without_".Length..];
        }
    }

    private static string Normalize(string value) =>
        value.Trim().Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();

    private static bool ContainsTerm(string value, string term) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(term, StringComparer.Ordinal);
}
