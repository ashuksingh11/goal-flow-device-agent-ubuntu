using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace GoalFlow.Device.Agent;

/// <summary>
/// One of the four places this agent talks to the model.
///
/// <para>
/// THE SITE IS THE UNIT, NOT THE REQUEST. Decompose's strict-JSON fallback is still decompose,
/// and grounding's three retry attempts are still grounding: a setting follows the QUESTION
/// being asked, not the shape of the round-trip that asks it. That is why
/// <c>GetStrictComposeContentAsync</c> takes a site rather than inventing a fifth one.
/// </para>
///
/// <para>
/// This also replaces the two bare <c>"decompose"</c>/<c>"compose"</c> string constants the
/// json-mode memory used to key on: the per-site JSON-mode verdict and the per-site routing are
/// the same distinction, so they key off the same object.
/// </para>
/// </summary>
public sealed record LlmCallSite(string Name)
{
    public static readonly LlmCallSite Decompose = new("decompose");
    public static readonly LlmCallSite Grounding = new("grounding");
    public static readonly LlmCallSite Compose = new("compose");
    public static readonly LlmCallSite Adapt = new("adapt");

    public static readonly IReadOnlyList<LlmCallSite> All =
        new[] { Decompose, Grounding, Compose, Adapt };

    public override string ToString() => Name;
}

/// <summary>
/// The two OpenRouter body fields that decide how fast this agent runs: <c>provider</c> (routing
/// preferences, process-wide) and <c>reasoning_effort</c> (per call site).
///
/// <para>
/// THEY TRAVEL BY DIFFERENT ROADS. <c>reasoning_effort</c> is a real
/// <see cref="OpenAIPromptExecutionSettings"/> property on SK 1.43, so it is set here.
/// <c>provider</c> is not modelled by SK until 1.78, and both device repos are pinned to 1.43
/// (Tizen 12 ships its own System.Text.Json 8.x as a platform assembly, and SK ≥ 1.61 wants
/// 10.x — so the Hub cannot move, and Ubuntu matches it on purpose so the dev box exercises the
/// SK the Hub runs). It therefore rides on the HttpClient instead, via
/// <see cref="OpenRouterBodyHandler"/>, which works on any SK version.
/// </para>
///
/// <para>
/// WHY THIS EXISTS — MEASURED, v8-M0. With no <c>provider</c> field OpenRouter load-balances
/// across nineteen endpoints whose throughput spans 39x. Four identical standalone runs on the
/// same afternoon took 59s, 175s, 145s and 189s; benchmarking the same compose-shaped task showed
/// why: unpinned landed on CoreWeave (52 tok/s) and Novita (76 tok/s), while Cerebras ran it at
/// 1523 tok/s — 50.1s versus 1.5s for identical work. The demo's latency was never really a
/// modelling problem. It was a routing default nobody had ever set.
/// </para>
///
/// <para>
/// AND WHY <c>reasoning_effort</c> IS BUILT BUT LEFT OFF. The same benchmark measured
/// <c>low</c> on every provider: reasoning tokens collapse from ~1400 to 26-89 and the model
/// stops being able to do the job — every single <c>low</c> run returned an invalid plan. Nor is
/// there anything to win: <c>medium</c> and the provider default are within 0.2s of each other
/// once the provider is fast (Cerebras 1.7s vs 1.5s). So the knob is here, documented and
/// verified, and it sends nothing. The next person to reach for it should read this paragraph
/// first and re-run the benchmark rather than assume "less reasoning must be faster".
/// </para>
///
/// <para>
/// UNSET IS A NO-OP, AND THAT IS LOAD-BEARING. <see cref="None"/> writes nothing at all: no
/// <c>ReasoningEffort</c>, and no handler is even installed, so SK builds its own HttpClient
/// exactly as it did before this type existed and the request body is byte-for-byte unchanged.
/// Every gate in <c>verify/</c> runs with these variables unset and was written against that body.
/// </para>
///
/// <para>
/// IMMUTABLE AND SHARED. One instance is built at startup and read by every goal on every thread.
/// <c>provider</c> is held as a <see cref="JsonElement"/> cloned off a parsed document — not a
/// <see cref="JsonObject"/> — because a JsonObject built by PARSING materialises its child
/// dictionary lazily on first read, which is an unsynchronised write the moment two goals plan at
/// once. A cloned JsonElement owns a private, fully-parsed buffer with no lazy state.
/// </para>
///
/// <para>
/// "CEREBRAS OR NOTHING" is expressed with the two knobs together:
/// <c>OPENROUTER_PROVIDER_ORDER=cerebras</c> plus
/// <c>OPENROUTER_PROVIDER_ALLOW_FALLBACKS=false</c>. That combination is what the demo ships,
/// and the reason is measured: Cerebras plans a goal in 8-10s and the next-best provider takes
/// 203-234s — slower than sending no preference at all. Falling back is not degrading gracefully
/// here, it is stalling for four minutes in front of an audience, so the demo would rather fail
/// visibly and be re-run.
/// </para>
/// </summary>
public sealed class LlmRouting
{
    /// <summary>Send nothing. The default, and what every verify gate gets.</summary>
    public static readonly LlmRouting None =
        new(null, null, new Dictionary<string, string?>(StringComparer.Ordinal));

    private readonly JsonElement? _provider;
    private readonly string? _defaultEffort;

    /// <summary>Site name to effort. A present key with a NULL value means "explicitly off here".</summary>
    private readonly IReadOnlyDictionary<string, string?> _perSiteEffort;

    private LlmRouting(JsonElement? provider, string? defaultEffort, IReadOnlyDictionary<string, string?> perSiteEffort)
    {
        _provider = provider;
        _defaultEffort = defaultEffort;
        _perSiteEffort = perSiteEffort;
    }

    /// <summary>True when this adds nothing to any request body.</summary>
    public bool IsNoOp => _provider is null && _defaultEffort is null && _perSiteEffort.Count == 0;

    /// <summary>
    /// Stamp the routing fields onto ONE freshly-built settings object and hand it back, so a call
    /// site stays a single expression.
    ///
    /// <para>
    /// CALL THIS AT CONSTRUCTION, NEVER INSIDE A RETRY LOOP. SK calls
    /// <c>PromptExecutionSettings.Freeze()</c> on the object during the first request, so a second
    /// Apply on the same instance would throw. Returning the object you are initialising is what
    /// makes the correct usage the natural one — and it is why a retried request is byte-identical
    /// to the first.
    /// </para>
    /// </summary>
    public T Apply<T>(T settings, LlmCallSite site) where T : OpenAIPromptExecutionSettings
    {
        // `provider` is NOT set here — it rides on the HttpClient, via OpenRouterBodyHandler,
        // because SK 1.43 has no ExtraBody. reasoning_effort DOES exist on 1.43, so it stays a
        // normal setting.
        if (EffortFor(site) is { } effort)
        {
            settings.ReasoningEffort = effort;
        }

        return settings;
    }

    /// <summary>The configured OpenRouter <c>provider</c> block, or null when none is set.</summary>
    public JsonElement? Provider => _provider;

    /// <summary>Per-site value first (including an explicit off), then the global default.</summary>
    public string? EffortFor(LlmCallSite site)
        => _perSiteEffort.TryGetValue(site.Name, out var perSite) ? perSite : _defaultEffort;

    /// <summary>What this will actually send, for the one startup log line.</summary>
    public string Describe()
    {
        if (IsNoOp)
        {
            return "off";
        }

        var provider = _provider is { } p ? p.GetRawText() : "-";
        var efforts = string.Join(" ", LlmCallSite.All.Select(s => $"{s.Name}={EffortFor(s) ?? "-"}"));
        return $"provider={provider} reasoning_effort[{efforts}]";
    }

    /// <summary>
    /// Read the routing from the environment.
    ///
    /// <para>
    /// Takes a reader rather than calling <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// itself so the gate can hand it a fixed map and stay hermetic. That matters more than it
    /// looks: <c>DotEnv.Load</c> has already splatted the developer's .env into the process by the
    /// time any verifier runs, so a gate asserting "unset means we send nothing" would otherwise
    /// be asserting against whatever happens to be in that file.
    /// </para>
    ///
    /// <para>FAIL-SOFT: a malformed value is logged and ignored, never thrown. Planning with
    /// default routing beats not planning.</para>
    /// </summary>
    public static LlmRouting FromEnvironment(Func<string, string?> read, ILogger? log = null)
    {
        var provider = ReadProvider(read, log);
        var perSite = new Dictionary<string, string?>(StringComparer.Ordinal);

        string? defaultEffort = null;
        if (TryReadEffort(read, "LLM_REASONING_EFFORT", log, out var globalEffort))
        {
            defaultEffort = globalEffort;
        }

        foreach (var site in LlmCallSite.All)
        {
            var key = "LLM_REASONING_EFFORT_" + site.Name.ToUpperInvariant();
            if (TryReadEffort(read, key, log, out var siteEffort))
            {
                perSite[site.Name] = siteEffort;
            }
        }

        return provider is null && defaultEffort is null && perSite.Count == 0
            ? None
            : new LlmRouting(provider, defaultEffort, perSite);
    }

    private static JsonElement? ReadProvider(Func<string, string?> read, ILogger? log)
    {
        var raw = Clean(read("OPENROUTER_PROVIDER_JSON"));
        if (raw is null)
        {
            var order = (Clean(read("OPENROUTER_PROVIDER_ORDER")) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (order.Length == 0)
            {
                return null;
            }

            // The MECHANISM defaults true, but the demo ships it FALSE. Measured on the real
            // pipeline: Cerebras plans a goal in 8-10s, the next-best provider takes 203-234s —
            // slower than sending no preference at all. Falling back is not a degrade here, it
            // is a four-minute stall in front of an audience.
            var allowFallbacks = !string.Equals(
                Clean(read("OPENROUTER_PROVIDER_ALLOW_FALLBACKS")), "false", StringComparison.OrdinalIgnoreCase);

            raw = new JsonObject
            {
                ["order"] = new JsonArray(order.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
                ["allow_fallbacks"] = allowFallbacks,
            }.ToJsonString();
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                log?.LogWarning("llm_routing_ignored provider must be a JSON object, got {Kind}", doc.RootElement.ValueKind);
                return null;
            }

            // Clone() detaches from the document's pool-rented buffer, so the element stays valid
            // and thread-safe for the life of the process.
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            log?.LogWarning("llm_routing_ignored provider is not valid JSON: {Message}", ex.Message);
            return null;
        }
    }

    private static readonly string[] KnownEfforts = { "minimal", "low", "medium", "high" };

    /// <summary>
    /// false = the variable is unset, say nothing. true with a null effort = it says "off", which
    /// is a DIFFERENT thing: it suppresses the global default at that one site.
    /// </summary>
    private static bool TryReadEffort(Func<string, string?> read, string key, ILogger? log, out string? effort)
    {
        effort = null;
        var raw = Clean(read(key));
        if (raw is null)
        {
            return false;
        }

        if (string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lowered = raw.ToLowerInvariant();
        if (!KnownEfforts.Contains(lowered))
        {
            log?.LogWarning("llm_routing_ignored {Key}={Value} is not one of {Known} or off",
                key, raw, string.Join("|", KnownEfforts));
            return false;
        }

        effort = lowered;
        return true;
    }

    private static string? Clean(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
