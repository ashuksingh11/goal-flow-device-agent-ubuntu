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
/// The two OpenRouter body fields that decide how fast this agent runs, neither of which appears
/// in an <see cref="OpenAIPromptExecutionSettings"/> object initializer: <c>provider</c> (routing
/// preferences, process-wide) and <c>reasoning_effort</c> (per call site).
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
/// UNSET IS A NO-OP, AND THAT IS LOAD-BEARING. <see cref="None"/> writes nothing at all — no
/// <c>ExtraBody</c> dictionary, no <c>ReasoningEffort</c> — so the request body is byte-for-byte
/// what it was before this type existed. Every gate in <c>verify/</c> runs with these variables
/// unset and was written against that body.
/// </para>
///
/// <para>
/// IMMUTABLE AND SHARED. One instance is built at startup and read by every goal on every thread.
/// <c>provider</c> is held as a <see cref="JsonElement"/> cloned off a parsed document — not a
/// <see cref="JsonObject"/> — because a JsonObject built by PARSING materialises its child
/// dictionary lazily on first read, which is an unsynchronised write the moment two goals plan at
/// once. A cloned JsonElement owns a private, fully-parsed buffer with no lazy state, and SK only
/// ever reads it.
/// </para>
///
/// <para>
/// REQUIRES SK 1.78.0+ for <c>ExtraBody</c> (absent in 1.70 and earlier); <c>ReasoningEffort</c>
/// goes back to 1.61. Both reach the wire through the one
/// <c>ClientCore.CreateChatCompletionOptions</c> shared by the streaming and non-streaming paths,
/// so this works for the grounding stream and the compose call alike.
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
        if (_provider is { } provider && ExtraBodyProperty is { } extraBody)
        {
            var bag = extraBody.GetValue(settings) as IDictionary<string, object?>;
            if (bag is null)
            {
                bag = new Dictionary<string, object?>(StringComparer.Ordinal);
                extraBody.SetValue(settings, bag);
            }
            bag["provider"] = provider;
        }

        if (EffortFor(site) is { } effort)
        {
            settings.ReasoningEffort = effort;
        }

        return settings;
    }

    /// <summary>
    /// <c>OpenAIPromptExecutionSettings.ExtraBody</c>, or null on an SK line that predates it.
    ///
    /// <para>
    /// REFLECTION, BECAUSE THE TWO DEVICE REPOS CANNOT AGREE ON AN SK VERSION. Ubuntu runs SK
    /// 1.78, where <c>ExtraBody</c> exists and is <c>[Experimental("SKEXP0010")]</c>. Tizen is
    /// pinned to 1.43 and cannot move: SK ≥ 1.61 depends on System.Text.Json 10.x, and Tizen 12
    /// ships its own STJ 8.x as a platform assembly loaded before app-local ones, so the newer
    /// package simply refuses to load on the Hub. A compile-time reference would therefore break
    /// the Tizen build, and the core is deliberately kept byte-identical between the repos.
    /// </para>
    ///
    /// <para>
    /// <c>ReasoningEffort</c> needs no such treatment — it exists on both lines (checked).
    /// </para>
    ///
    /// <para>
    /// On Tizen the equivalent of provider pinning is the model slug itself: setting
    /// <c>OPENROUTER_MODEL=openai/gpt-oss-120b:nitro</c> in <c>goalflow.conf</c> asks OpenRouter
    /// to sort by throughput, which needs no request field and therefore no SK support. It is
    /// less precise than naming providers in order, but it is the same idea and it is one line.
    /// </para>
    /// </summary>
    private static readonly System.Reflection.PropertyInfo? ExtraBodyProperty =
        typeof(OpenAIPromptExecutionSettings).GetProperty("ExtraBody");

    /// <summary>
    /// True when a <c>provider</c> preference was configured but this SK build cannot send it —
    /// so the caller can say so instead of silently running unpinned.
    /// </summary>
    public bool ProviderUnsupported => _provider is not null && ExtraBodyProperty is null;

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
        // Loud, because the failure mode is silence: a Hub configured to prefer Cerebras but
        // unable to say so would just run at unpinned speed and look like the config took.
        var caveat = ProviderUnsupported
            ? " !! this Semantic Kernel build has no ExtraBody, so the provider preference is NOT being sent"
              + " — use OPENROUTER_MODEL=<model>:nitro instead"
            : "";
        return $"provider={provider} reasoning_effort[{efforts}]{caveat}";
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

            // allow_fallbacks defaults TRUE, deliberately. false turns "prefer these providers"
            // into "these providers or a 404", and a plan that came from Groq instead of Cerebras
            // is a demo that ran; a plan that failed because Cerebras was busy is not.
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
