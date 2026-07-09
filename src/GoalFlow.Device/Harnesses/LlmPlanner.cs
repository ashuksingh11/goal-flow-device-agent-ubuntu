using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// LLM planner: Microsoft Semantic Kernel over OpenRouter (OpenAI-compatible
/// chat-completion endpoint). Build effort: FULL logic later.
/// <para>
/// The prompt serializes contract + world-state and asks for a
/// <see cref="CandidatePlan"/>-shaped JSON. The output is treated as
/// UNTRUSTED: it still goes through the deterministic safety gate — the LLM
/// never self-certifies ("LLM plans, code checks"). On any failure
/// (no key, network, malformed JSON) the pipeline falls back per DI config
/// to <see cref="RulesPlanner"/> or <see cref="ScriptedPlanner"/>.
/// </para>
/// </summary>
public sealed class LlmPlanner : IPlanner
{
    private readonly LlmPlannerOptions _options;
    private readonly ITrace _trace;

    // TODO(impl): inject Microsoft.SemanticKernel.Kernel built with an
    // OpenAI-compatible chat completion connector pointed at _options.BaseUrl.

    public LlmPlanner(LlmPlannerOptions options, ITrace trace)
    {
        _options = options;
        _trace = trace;
    }

    public Task<CandidatePlan> CreatePlanAsync(
        Dispatch contract,
        WorldState world,
        CancellationToken cancellationToken = default) =>
        // TODO:
        //  1. Build system+user prompt from contract (incl. soft constraints as
        //     hints) and world (inventory, expiring-soon, calendar, recipes).
        //  2. Invoke SK chat completion (model = _options.Model).
        //  3. Parse strict-JSON response into CandidatePlan (PlannerId = "llm").
        //  4. Trace prompt/response summary; throw PlannerUnavailable-style
        //     exception on failure so the pipeline can fall back.
        throw new NotImplementedException("Design stub.");
}

/// <summary>
/// OpenRouter connection settings, bound from environment
/// (see .env.example).
/// </summary>
public sealed record LlmPlannerOptions
{
    /// <summary>OPENROUTER_BASE_URL.</summary>
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";

    /// <summary>OPENROUTER_MODEL.</summary>
    public string Model { get; init; } = "anthropic/claude-sonnet-5";

    /// <summary>OPENROUTER_API_KEY.</summary>
    public string? ApiKey { get; init; }
}
