using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Harness;

/// <summary>
/// HARNESS GUARD: the same read, asked twice, is answered from what it already said.
///
/// <para>
/// A Semantic Kernel <see cref="IFunctionInvocationFilter"/> that remembers every
/// grounding read of the current pass by (function, arguments) and short-circuits an
/// IDENTICAL repeat with a one-line pointer instead of re-running it.
/// </para>
///
/// <para>
/// WHY THIS EXISTS. Grounding is one streaming call with auto-invoke, and every tool
/// call inside it is a separate round-trip to the model. That makes a repeated read
/// expensive in the only currency that matters here — wall-clock in front of a user
/// watching a progress panel. v7.1 measured a meal plan spending <b>four and a half
/// minutes</b> on ten-plus calls to <c>Recipes.FindRecipes</c> whose arguments differed
/// only in the ORDER of the tag list: the model was hunting for a white-meat recipe in a
/// box that has none, and each identical answer read as "try rephrasing".
/// </para>
///
/// <para>
/// <see cref="Products.FamilyHub.RecipePlugin"/> fixes that particular lie at
/// the source, by naming the tags it could not match. This filter is the GENERIC net
/// underneath it: no read tool, present or future, can cost more than one round-trip per
/// distinct question. It is deliberately a cheap, exact-match memo — it does not try to
/// judge whether two different-looking queries mean the same thing, because guessing that
/// is how a guard starts hiding real reads.
/// </para>
///
/// <para>
/// SCOPE AND SAFETY. Reset per goal run (<see cref="Reset"/>), so nothing survives into a
/// later plan or a later day. Grounding functions are READ-ONLY by construction — the
/// planner is given no side-effecting tools — so within one pass an identical read cannot
/// have a different answer, and returning the memo is not a stale result, it is the same
/// result. It is registered AFTER <see cref="SafetyFilter"/>, so the safety gate has
/// already run on every call this ever sees.
/// </para>
/// </summary>
public sealed class RepeatReadFilter : IFunctionInvocationFilter
{
    private readonly ILogger<RepeatReadFilter> _logger;
    private readonly ConcurrentDictionary<string, string> _answered = new(StringComparer.Ordinal);

    private Trace? _trace;

    public RepeatReadFilter(ILogger<RepeatReadFilter> logger) => _logger = logger;

    /// <summary>How many repeats this pass suppressed — reported as a grounding verdict.</summary>
    public int SuppressedCount { get; private set; }

    /// <summary>New goal, empty memory. Called at the top of every plan run.</summary>
    public void Reset(Trace? trace = null)
    {
        _answered.Clear();
        SuppressedCount = 0;
        _trace = trace;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var key = KeyFor(context);

        if (_answered.TryGetValue(key, out var previous))
        {
            SuppressedCount++;
            _logger.LogInformation("repeat_read_suppressed {Plugin}.{Function}", context.Function.PluginName, context.Function.Name);
            // Say it in the transcript. A suppressed call that vanished silently would make
            // the run look like it skipped a read it was asked for.
            if (_trace is not null)
            {
                await _trace.ThinkingStepAsync(
                    $"Already read {context.Function.PluginName}.{context.Function.Name}",
                    "same arguments as before — reusing that answer rather than asking twice");
            }
            // The model gets its data back (so nothing downstream is missing) plus a plain
            // instruction. Returning ONLY a scolding would leave it without the facts and
            // invite yet another call.
            context.Result = new FunctionResult(context.Function,
                $$"""
                {"repeat_of_previous_call": true, "note": "You already called this with exactly these arguments. Nothing in the world has changed during this planning pass, so the answer is unchanged and is repeated below. Do not call it again — use it.", "result": {{previous}}}
                """);
            return;
        }

        await next(context);

        // Only a SUCCESSFUL read is worth memoizing. A blocked or failed call must be free
        // to be retried — the safety filter's refusal in particular is a message the model
        // is supposed to be able to act on and re-attempt differently.
        var rendered = context.Result?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(rendered))
        {
            _answered[key] = rendered;
        }
    }

    /// <summary>
    /// (plugin, function, every argument name=value) — identity up to ORDER, which is the
    /// whole point.
    ///
    /// <para>
    /// Two normalizations, and the second is the one that earns this method. Argument PAIRS
    /// are sorted because a dictionary's order is not stable across calls. Then any value
    /// that is itself a JSON array has its ELEMENTS sorted — because the measured loop asked
    /// the identical question four times as <c>["high_protein","quick_prep","white_meat"]</c>,
    /// <c>["high_protein","white_meat","quick_prep"]</c>, <c>["white_meat","high_protein",
    /// "quick_prep"]</c>… A key that compared those as strings would have called every one of
    /// them a new question and suppressed nothing.
    /// </para>
    ///
    /// <para>
    /// Sorting a list is only safe because these are SET-like arguments — tags to prefer,
    /// ingredients to exclude — where order carries no meaning. It is applied to the key
    /// alone; the arguments the function actually receives are untouched.
    /// </para>
    /// </summary>
    private static string KeyFor(FunctionInvocationContext context)
    {
        var args = context.Arguments
            .Select(a => $"{a.Key}={NormalizeValue(a.Value)}")
            .OrderBy(s => s, StringComparer.Ordinal);
        return $"{context.Function.PluginName}.{context.Function.Name}({string.Join("&", args)})";
    }

    /// <summary>A JSON array value, order-normalized; anything else rendered as-is.</summary>
    private static string NormalizeValue(object? value)
    {
        var raw = value?.ToString() ?? "";
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('['))
        {
            return raw;
        }
        try
        {
            var array = System.Text.Json.Nodes.JsonNode.Parse(raw)?.AsArray();
            if (array is null)
            {
                return raw;
            }
            var items = array.Select(n => n?.ToJsonString() ?? "null").OrderBy(s => s, StringComparer.Ordinal);
            return $"[{string.Join(",", items)}]";
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON after all — compare it as the string it is rather than guessing.
            return raw;
        }
    }
}
