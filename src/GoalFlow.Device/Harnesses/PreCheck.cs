namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Sense-phase harness: validates access/permissions before the pipeline
/// touches any local API. Build effort: HONEST STUB — in the POC it is a
/// pass-through that logs "access validated"; named here so the real
/// permission model has a seam to land in.
/// </summary>
public interface IPreCheck
{
    /// <summary>
    /// Verifies the task may access the local capabilities it needs.
    /// POC behavior: always allowed, with a trace entry.
    /// </summary>
    Task<PreCheckResult> ValidateAsync(GoalTask task, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of the pre-check.</summary>
public sealed record PreCheckResult
{
    public required bool Allowed { get; init; }

    /// <summary>Human-readable explanation, e.g. "access validated".</summary>
    public required string Note { get; init; }
}

/// <summary>Honest stub: pass-through that logs "access validated" to the trace.</summary>
public sealed class PassThroughPreCheck : IPreCheck
{
    private readonly ITrace _trace;

    public PassThroughPreCheck(ITrace trace) => _trace = trace;

    public Task<PreCheckResult> ValidateAsync(GoalTask task, CancellationToken cancellationToken = default) =>
        // TODO: trace "access validated"; return Allowed = true.
        throw new NotImplementedException("Design stub.");
}
