namespace GoalFlow.Device.Harness;

/// <summary>Whether a runtime condition holds right now.</summary>
public enum PrecheckStatus
{
    /// <summary>The condition holds.</summary>
    Pass,

    /// <summary>Degraded but workable — proceed, and say so.</summary>
    Warn,

    /// <summary>Does not hold. The work cannot happen yet.</summary>
    Fail,

    /// <summary>Not applicable here (e.g. the probe needs a parameter it wasn't given).</summary>
    Skipped
}

/// <summary>One probe's verdict, with a reason a person could act on.</summary>
public sealed record PrecheckResult(string Id, PrecheckStatus Status, string? Detail = null)
{
    public bool Blocks => Status == PrecheckStatus.Fail;

    public static PrecheckResult Pass(string id) => new(id, PrecheckStatus.Pass);
    public static PrecheckResult Fail(string id, string detail) => new(id, PrecheckStatus.Fail, detail);
    public static PrecheckResult Skip(string id, string detail) => new(id, PrecheckStatus.Skipped, detail);
}

/// <summary>What every probe in a run was asked and answered.</summary>
public sealed record PrecheckReport(IReadOnlyList<PrecheckResult> Results)
{
    public static PrecheckReport Empty { get; } = new([]);

    /// <summary>True when nothing failed. Warnings do not block.</summary>
    public bool Ok => !Results.Any(r => r.Blocks);

    public IReadOnlyList<PrecheckResult> Failures => Results.Where(r => r.Blocks).ToArray();

    /// <summary>One line a person can act on: what's wrong and therefore what to fix.</summary>
    public string Remediation => string.Join("; ", Failures.Select(f => f.Detail ?? f.Id));
}

/// <summary>
/// Checks one runtime condition: is the camera working, is SmartThings reachable,
/// is the oven online.
///
/// <para>
/// WHY THIS IS NOT THE SAFETY FILTER, which is the question the design keeps
/// inviting: they answer different questions and deserve different answers.
/// Safety says "this must never happen" — a refusal, forever, about what the
/// agent is allowed to do. A precheck says "this cannot happen YET" — about what
/// the world currently permits. Conflating them would teach the model to re-plan
/// around a temporary outage as though it were forbidden (dropping the oven step
/// entirely, rather than waiting for the oven), and would tell the user their
/// house rules blocked something when actually a device was unplugged.
/// </para>
///
/// <para>
/// So prechecks run at PHASE BOUNDARIES (before planning, before actuating), not
/// inside the kernel's invocation pipeline. The filter stays safety-only.
/// </para>
///
/// <para>
/// Probes are product knowledge — only the Family Hub knows it has a camera —
/// so they live in the pack. The harness owns when they run and what a failure
/// means.
/// </para>
/// </summary>
public interface IPrecheckProbe
{
    /// <summary>
    /// The id bound in the pack's <c>prechecks.json</c> — "internet",
    /// "appliance_online". A probe may take a PARAMETER after a colon
    /// ("appliance_online:thermostat"): the binding names the id, the engine passes
    /// the argument, so one probe serves every appliance.
    /// </summary>
    string Id { get; }

    Task<PrecheckResult> RunAsync(string? argument, CancellationToken ct = default);
}
