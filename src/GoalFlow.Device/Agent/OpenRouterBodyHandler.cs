using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoalFlow.Device.Agent;

/// <summary>
/// Adds OpenRouter's <c>provider</c> block to every chat-completion request, by editing the JSON
/// body on its way out.
///
/// <para>
/// WHY NOT SEMANTIC KERNEL'S OWN <c>ExtraBody</c>. Because both device repos are pinned to SK
/// 1.43, and <c>ExtraBody</c> arrived in 1.78. The pin is not negotiable on Tizen — SK ≥ 1.61
/// depends on System.Text.Json 10.x, and Tizen 12 ships its own STJ 8.x as a platform assembly
/// that is loaded before app-local ones, so the newer package simply refuses to load on the Hub.
/// Ubuntu then matches that pin deliberately, so the dev box exercises the same SK the Hub runs
/// rather than a newer one that happens to be on NuGet.
/// </para>
///
/// <para>
/// The alternative was the model slug — <c>openai/gpt-oss-120b:nitro</c>, which asks OpenRouter to
/// sort by throughput and needs no request field at all. It works, and it was measured landing on
/// Cerebras. It was rejected because it cannot express <c>only</c>: <c>:nitro</c> always allows
/// fallbacks, and a fallback here is not graceful. Measured on the real pipeline, Cerebras plans a
/// goal in 8-10s and the next-best provider takes 203-234s — SLOWER than sending no preference at
/// all. A silent four-minute stall in front of an audience is worse than a visible error, so the
/// demo pins hard and this handler is what makes <c>allow_fallbacks: false</c> expressible.
/// </para>
///
/// <para>
/// SCOPE, deliberately narrow: it only ever ADDS the one top-level key, only on POSTs whose body
/// is a JSON object, and never rewrites anything SK put there. Streaming and non-streaming share
/// one <see cref="HttpClient"/>, so both are covered by construction.
/// </para>
/// </summary>
internal sealed class OpenRouterBodyHandler : DelegatingHandler
{
    private readonly JsonElement _provider;

    public OpenRouterBodyHandler(JsonElement provider, HttpMessageHandler inner) : base(inner)
        => _provider = provider;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.Content is not null)
        {
            var raw = await request.Content.ReadAsStringAsync(cancellationToken);
            if (JsonNode.Parse(raw) is JsonObject body)
            {
                body["provider"] = JsonNode.Parse(_provider.GetRawText());
                var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
                // Replacing Content resets Content-Length for us; copying the media type keeps the
                // charset SK chose rather than assuming utf-8 twice.
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, mediaType);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Build the <see cref="HttpClient"/> SK should use, or null when no provider preference is
    /// configured — in which case the caller passes nothing and SK builds its own, exactly as
    /// before this type existed.
    ///
    /// <para>
    /// <c>Timeout = InfiniteTimeSpan</c> IS LOAD-BEARING. Handing SK an HttpClient means owning
    /// its timeout, and the default is 100 seconds — which would silently cap
    /// <c>LLM_CALL_TIMEOUT_SECONDS</c> (180) and <c>LLM_STREAM_TIMEOUT_SECONDS</c> (210) without
    /// touching either constant. This agent enforces its deadlines with linked
    /// <see cref="CancellationTokenSource"/>s instead (see <c>GoalAgent.Deadline</c>, and gate 15,
    /// which exists because a hung stream once cost hours), so the HttpClient must not have an
    /// opinion of its own.
    /// </para>
    /// </summary>
    public static HttpClient? CreateClient(LlmRouting routing)
        => routing.Provider is { } provider
            ? new HttpClient(new OpenRouterBodyHandler(provider, new HttpClientHandler()))
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            }
            : null;
}
