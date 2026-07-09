using GoalFlow.Device.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private readonly IPlanner _fallback;
    private readonly ITrace _trace;
    private readonly IClock _clock;

    public LlmPlanner(LlmPlannerOptions options, IPlanner fallback, ITrace trace, IClock clock)
    {
        _options = options;
        _fallback = fallback;
        _trace = trace;
        _clock = clock;
    }

    public async Task<CandidatePlan> CreatePlanAsync(
        Dispatch contract,
        WorldState world,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                throw new InvalidOperationException("OPENROUTER_API_KEY is not set.");
            }

            var endpoint = new Uri(_options.BaseUrl.TrimEnd('/'));
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://goalflow.local/device-agent");
            httpClient.DefaultRequestHeaders.Add("X-Title", "GoalFlow Device Agent");

            var builder = Kernel.CreateBuilder();
            builder.AddOpenAIChatCompletion(
                modelId: _options.Model,
                endpoint: endpoint,
                apiKey: _options.ApiKey,
                orgId: null,
                serviceId: null,
                httpClient: httpClient);

            var kernel = builder.Build();
            var chat = kernel.Services.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory("""
                You are the GoalFlow on-device meal planner. Return ONLY strict JSON.
                Shape: {"plan":[{"day":"Mon","dish":"recipe name","why":["tag"]}],"proposals":[{"proposal_id":"p1","action":"add_to_shopping_list","items":["item"],"reason":"needed for dishes","requires_approval":true}]}
                Use only recipe names present in the world_state.recipes list. Do not include markdown.
                Planner output is not a safety decision; deterministic code will check hard constraints after you respond.
                """);
            history.AddUserMessage($"""
                contract:
                {JsonSerializer.Serialize(contract, ContractJson.Options)}

                world_state:
                {JsonSerializer.Serialize(world, ContractJson.Options)}

                Produce one dinner per contract.scope.days. Consolidate missing ingredients into one add_to_shopping_list proposal.
                Prefer soft preferences, reduce waste by using expiring inventory, and choose quick/light recipes on evening calendar-event days.
                Return JSON only.
                """);

            var response = await chat.GetChatMessageContentAsync(
                history,
                new OpenAIPromptExecutionSettings
                {
                    MaxTokens = 2200,
                    Temperature = 0,
                },
                kernel,
                cancellationToken);

            var content = response.Content ?? throw new InvalidOperationException("LLM returned empty content.");
            var candidate = ParseCandidate(content);
            _trace.Record(new TraceEvent
            {
                At = _clock.Now,
                GoalId = contract.GoalId,
                Phase = TracePhase.Decide,
                Source = nameof(LlmPlanner),
                Kind = "llm_plan_created",
                Message = $"OpenRouter model {_options.Model} returned {candidate.Plan.Count} meals",
            });
            return candidate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _trace.Record(new TraceEvent
            {
                At = _clock.Now,
                GoalId = contract.GoalId,
                Phase = TracePhase.Decide,
                Source = nameof(LlmPlanner),
                Kind = "planner_fallback",
                Message = $"LLM planner failed; falling back to rules: {ex.GetType().Name}: {ex.Message}",
            });
            var fallback = await _fallback.CreatePlanAsync(contract, world, cancellationToken);
            return fallback with { PlannerId = $"{fallback.PlannerId}_fallback_from_llm" };
        }
    }

    private static CandidatePlan ParseCandidate(string content)
    {
        var json = ExtractJson(content);
        var parsed = JsonSerializer.Deserialize<LlmPlanJson>(json, ContractJson.Options)
            ?? throw new JsonException("LLM JSON did not parse.");

        if (parsed.Plan is null || parsed.Plan.Count == 0)
        {
            throw new JsonException("LLM JSON contains no plan items.");
        }

        return new CandidatePlan
        {
            Plan = parsed.Plan,
            Proposals = parsed.Proposals ?? [],
            PlannerId = "llm",
        };
    }

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        if (first < 0 || last < first)
        {
            throw new JsonException("LLM response did not contain a JSON object.");
        }

        return trimmed[first..(last + 1)];
    }

    private sealed record LlmPlanJson
    {
        [JsonPropertyName("plan")]
        public IReadOnlyList<PlanItem>? Plan { get; init; }

        [JsonPropertyName("proposals")]
        public IReadOnlyList<ProposalItem>? Proposals { get; init; }
    }
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
