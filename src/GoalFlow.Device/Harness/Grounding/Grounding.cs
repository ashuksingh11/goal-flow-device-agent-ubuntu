using GoalFlow.Device.Contracts;
using System.Text.Json.Nodes;
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
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var window = dispatch.TimeWindow ?? new TimeWindow
        {
            Start = today,
            End = _clock.Today.AddDays(6).ToString("yyyy-MM-dd")
        };

        var digest = string.Join("\n", [
            $"domain: {dispatch.Domain}",
            $"objective: {dispatch.Objective}",
            $"time_window: {window.Start}..{window.End}",
            "Use read-only tools to ground inventory, calendar, recipes, reminders, shopping list, guests, dietary constraints, and appliance state before finalizing."
        ]);

        return Task.FromResult(new GroundingContext
        {
            Today = today,
            Window = window,
            HardConstraintsJson = dispatch.Constraints.Hard.ToJsonString(ContractJson.Options),
            SoftConstraintsJson = dispatch.Constraints.Soft?.ToJsonString(ContractJson.Options),
            WorldDigest = digest
        });
    }

    /// <summary>Renders the context into the planner's system-prompt section.</summary>
    public string RenderPrompt(GroundingContext context)
        => $$"""
        You are the GoalFlow on-device Semantic Kernel agent.
        Today from the generic device clock is {{context.Today}}.
        Plan only inside the time window {{context.Window.Start}} to {{context.Window.End}}.
        Hard constraints, verbatim safety policy: {{context.HardConstraintsJson}}
        Soft preferences: {{context.SoftConstraintsJson ?? "{}"}}

        Grounding digest:
        {{context.WorldDigest}}

        You must use available read-only tools for factual grounding. During planning, side-effecting actions are not tools.
        Return only the requested JSON object in the final answer.
        """;
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
