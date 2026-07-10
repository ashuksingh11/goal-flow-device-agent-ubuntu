using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;

namespace GoalFlow.Device.Modules.Steering;

/// <summary>
/// HARNESS MODULE: Trace / Explain.
/// One sink, two audiences: (a) STRUCTURED LOGS via
/// Microsoft.Extensions.Logging — leveled, correlation-id-scoped, for
/// debuggability; (b) STREAMED <c>agent_event</c> frames over the WebSocket —
/// the live "watch it think" feed (phase / thinking / tool_call / tool_result
/// / plan_progress). Every emit does both. Seq is monotonic per goal.
/// </summary>
public sealed class Trace
{
    private readonly ILogger<Trace> _logger;
    private readonly Func<AgentEvent, Task> _emit;
    private int _seq;
    private string? _goalId;
    private string? _correlationId;

    /// <param name="emit">
    /// Transport hook — <c>WsClient.SendAsync</c> when connected, a stdout/no-op
    /// sink in offline <c>--contract</c> mode. Trace does not know transports.
    /// </param>
    public Trace(ILogger<Trace> logger, Func<AgentEvent, Task> emit)
    {
        _logger = logger;
        _emit = emit;
    }

    /// <summary>Starts a goal scope: resets seq, pins goal_id/correlation_id on every frame + log line.</summary>
    public IDisposable BeginGoalScope(string goalId, string? correlationId)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Emits a phase change (grounding | planning | checking | awaiting_approval).</summary>
    public Task PhaseAsync(string phase)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Streams a chunk of model thinking/narration text.</summary>
    public Task ThinkingAsync(string text)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Emits a tool_call chip as the kernel is about to invoke {module}.{function}(args).</summary>
    public Task ToolCallAsync(string module, string function, JsonObject? args)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Emits a tool_result chip with a short human summary of what came back.</summary>
    public Task ToolResultAsync(string module, string function, string summary)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Emits plan_progress as each plan item materializes.</summary>
    public Task PlanProgressAsync(PlanItem item)
        => throw new NotImplementedException("v2-M0 design skeleton");

    // TODO(M1): private Task EmitAsync(string kind, JsonObject payload)
    //   -> builds AgentEvent { GoalId=_goalId, CorrelationId=_correlationId, Seq=Interlocked.Increment(ref _seq), ... }
    //   -> _logger.Log(structured, with EventId per kind) AND await _emit(evt).
}
