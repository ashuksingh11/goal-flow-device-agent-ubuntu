using GoalFlow.Device.Contracts;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Steering;

/// <summary>
/// HARNESS MODULE: Context Grounding.
/// Assembles the planner's opening context from the REAL world so the LLM
/// plans over facts, not hallucinated state. Two halves:
/// (1) a pre-pass snapshot — clock date, time window resolved against the
/// clock, hard/soft constraints verbatim, a short world digest — rendered into
/// the system prompt; (2) the live half: the capability plugins themselves,
/// which the model calls mid-plan via auto function-calling for anything the
/// digest didn't cover.
/// </summary>
public sealed class Grounding
{
    private readonly IClock _clock;

    public Grounding(IClock clock) => _clock = clock;

    /// <summary>
    /// Builds the grounding block for one dispatch: resolves "today" and the
    /// contract's time window via the generic clock, injects constraints.hard
    /// VERBATIM (the model must see the same truth the filter enforces),
    /// soft constraints as preferences, and the domain + scope + context.
    /// </summary>
    public Task<GroundingContext> AssembleAsync(Dispatch dispatch, Kernel kernel, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Renders the context into the planner's system-prompt section.</summary>
    public string RenderPrompt(GroundingContext context)
        => throw new NotImplementedException("v2-M0 design skeleton");
}

/// <summary>The assembled ground truth handed to the planner.</summary>
public sealed record GroundingContext
{
    /// <summary>Resolved current date (ISO) from the generic clock.</summary>
    public required string Today { get; init; }

    /// <summary>Resolved plan window (ISO), relative to <see cref="Today"/>.</summary>
    public required TimeWindow Window { get; init; }

    /// <summary>constraints.hard, verbatim JSON (also armed on the SafetyFilter).</summary>
    public required string HardConstraintsJson { get; init; }

    /// <summary>constraints.soft, verbatim JSON, or null.</summary>
    public string? SoftConstraintsJson { get; init; }

    /// <summary>Short digest of world state (expiring items, busy evenings, ...).</summary>
    public required string WorldDigest { get; init; }
}
