using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;

namespace GoalFlow.Device.Harness;

/// <summary>
/// HARNESS COMPONENT 3: Pre-check Engine — is the world ready?
///
/// <para>
/// The other gates ask about intent: the Safety Policy Engine asks whether an
/// action is ALLOWED, the Approval Coordinator whether it is CONSENTED. Neither
/// asks whether it is currently POSSIBLE. A plan that preheats an unplugged oven
/// passes both and then fails in the kitchen — which is the worst place to find
/// out, because the user already approved it.
/// </para>
///
/// <para>
/// TWO GATES, at phase boundaries rather than in the kernel pipeline:
/// </para>
/// <list type="number">
/// <item><b>before planning</b> — if the world can't support this goal at all
/// (no account, no network), say so instead of planning something undeliverable.
/// The goal waits; it does not fail.</item>
/// <item><b>before actuating</b> — conditions drift between planning and
/// approval, and approval is exactly when the delay happens. A cleared proposal
/// whose precheck now fails is DEFERRED, not executed and not lost.</item>
/// </list>
///
/// <para>
/// The engine owns the schedule and the meaning; the product pack owns the probes
/// and (in <c>prechecks.json</c>) which calls need which.
/// </para>
/// </summary>
public sealed class PrecheckEngine
{
    private readonly IReadOnlyDictionary<string, IPrecheckProbe> _probes;
    private readonly PrecheckBindings _bindings;
    private readonly ILogger<PrecheckEngine> _logger;

    public PrecheckEngine(IEnumerable<IPrecheckProbe> probes, PrecheckBindings bindings, ILogger<PrecheckEngine> logger)
    {
        _probes = probes.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        _bindings = bindings;
        _logger = logger;
    }

    /// <summary>
    /// GATE 1: can this goal be planned at all? Runs the pack's goal-level checks
    /// (the ones that are true of the whole device — account, network) before the
    /// planner spends a single token.
    /// </summary>
    public Task<PrecheckReport> RunForDispatchAsync(Dispatch dispatch, CancellationToken ct = default)
        => RunAsync(_bindings.ForGoal, $"goal {dispatch.GoalId}", ct);

    /// <summary>
    /// GATE 2: can this approved effect happen right now? Runs the checks bound to
    /// this specific call — the oven must be online to preheat it.
    /// </summary>
    public Task<PrecheckReport> RunForProposalAsync(ProposalItem proposal, CancellationToken ct = default)
        => RunAsync(_bindings.For(proposal.Module, proposal.Function), $"{proposal.Module}.{proposal.Function}", ct);

    private async Task<PrecheckReport> RunAsync(IReadOnlyList<string> checks, string what, CancellationToken ct)
    {
        if (checks.Count == 0)
        {
            return PrecheckReport.Empty;
        }

        var results = new List<PrecheckResult>();
        foreach (var check in checks)
        {
            // "appliance_online:thermostat" → probe "appliance_online", argument
            // "thermostat". One probe serves every appliance.
            var split = check.IndexOf(':');
            var id = split < 0 ? check : check[..split];
            var argument = split < 0 ? null : check[(split + 1)..];

            if (!_probes.TryGetValue(id, out var probe))
            {
                // A binding naming a probe nobody implements is a config bug. It must
                // not silently pass — a precheck that quietly does nothing is worse
                // than no precheck, because it reads as a guarantee.
                results.Add(PrecheckResult.Fail(check, $"no probe implements '{id}' — check the pack's prechecks.json"));
                continue;
            }

            try
            {
                results.Add(await probe.RunAsync(argument, ct) with { Id = check });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A probe that throws has not told us the world is fine.
                results.Add(PrecheckResult.Fail(check, $"the check itself failed: {ex.Message}"));
            }
        }

        var report = new PrecheckReport(results);
        if (!report.Ok)
        {
            _logger.LogWarning("precheck_failed {What}: {Remediation}", what, report.Remediation);
        }
        else
        {
            _logger.LogDebug("precheck_passed {What} ({Count} check(s))", what, results.Count);
        }

        return report;
    }
}

/// <summary>
/// Which checks apply to what, from the product pack's <c>config/prechecks.json</c>.
///
/// <para>
/// Bindings, not code, for the same reason the safety rules are: the same
/// ApplianceControl plugin needs <c>appliance_online:oven</c> on a Family Hub and
/// something else entirely on another product. The harness must not know.
/// </para>
/// </summary>
public sealed class PrecheckBindings
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _byPattern;

    /// <summary>Checks that must hold for ANY goal on this device.</summary>
    public IReadOnlyList<string> ForGoal { get; }

    private PrecheckBindings(IReadOnlyList<string> forGoal, IReadOnlyDictionary<string, IReadOnlyList<string>> byPattern)
    {
        ForGoal = forGoal;
        _byPattern = byPattern;
    }

    public static PrecheckBindings Empty { get; } =
        new([], new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The checks for one call: exact ("Appliance.PreheatOven") plus module-wide
    /// ("Appliance.*"). Most specific and general both apply — a module-wide rule is
    /// a floor, not a default to be overridden.
    /// </summary>
    public IReadOnlyList<string> For(string module, string function)
    {
        var checks = new List<string>();
        if (_byPattern.TryGetValue($"{module}.*", out var moduleWide))
        {
            checks.AddRange(moduleWide);
        }

        if (_byPattern.TryGetValue($"{module}.{function}", out var exact))
        {
            checks.AddRange(exact);
        }

        return checks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Missing file = no prechecks (a product need not declare any); malformed = throw.</summary>
    public static PrecheckBindings Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"{path} is not a JSON object.");

        var forGoal = Strings(root["goal"]);
        var byPattern = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pattern, value) in root["capabilities"]?.AsObject() ?? [])
        {
            byPattern[pattern] = Strings(value);
        }

        return new PrecheckBindings(forGoal, byPattern);
    }

    private static IReadOnlyList<string> Strings(JsonNode? node)
        => node is JsonArray arr
            ? arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray()
            : [];
}
