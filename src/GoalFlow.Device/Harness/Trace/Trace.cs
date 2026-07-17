using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;

namespace GoalFlow.Device.Harness;

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

    /// <summary>
    /// The goal this async flow is narrating, and its own seq counter.
    ///
    /// <para>
    /// PER GOAL, on an AsyncLocal — this used to be three plain fields on a
    /// singleton, which broke silently as soon as two goals overlapped (and
    /// Program has always dispatched every frame on its own Task.Run). Starting
    /// goal B reset seq to 0 and re-pinned the goal id, so goal A's remaining
    /// events streamed out stamped with B's goal_id and a seq that had gone
    /// BACKWARDS. The UI dedupes on seq per goal, so it drops anything not
    /// greater than the last it saw: goal A's plan simply stopped appearing,
    /// with no error anywhere. Same failure mode as the SafetyFilter clobber,
    /// same fix.
    /// </para>
    /// </summary>
    private sealed class GoalScope
    {
        public required string GoalId { get; init; }
        public string? CorrelationId { get; init; }
        public int Seq;
    }

    private static readonly AsyncLocal<GoalScope?> Current = new();

    /// <param name="emit">
    /// Transport hook — <c>WsClient.SendAsync</c> when connected, a stdout/no-op
    /// sink in offline <c>--contract</c> mode. Trace does not know transports.
    /// </param>
    public Trace(ILogger<Trace> logger, Func<AgentEvent, Task> emit)
    {
        _logger = logger;
        _emit = emit;
    }

    /// <summary>
    /// Starts a goal scope: its OWN seq counter, and goal_id/correlation_id pinned
    /// on every frame + log line made inside this async flow. Disposing restores
    /// whatever scope was active before, so nesting is safe.
    /// </summary>
    public IDisposable BeginGoalScope(string goalId, string? correlationId)
    {
        var previous = Current.Value;
        Current.Value = new GoalScope { GoalId = goalId, CorrelationId = correlationId };
        var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["goal_id"] = goalId,
            ["correlation_id"] = correlationId
        }) ?? NullScope.Instance;
        return new Restore(logScope, previous);
    }

    /// <summary>Ends a goal scope and restores the previous one.</summary>
    private sealed class Restore : IDisposable
    {
        private readonly IDisposable _logScope;
        private readonly GoalScope? _previous;

        public Restore(IDisposable logScope, GoalScope? previous)
        {
            _logScope = logScope;
            _previous = previous;
        }

        public void Dispose()
        {
            Current.Value = _previous;
            _logScope.Dispose();
        }
    }

    /// <summary>Emits a phase change (grounding | planning | checking | awaiting_approval).</summary>
    public Task PhaseAsync(string phase)
        => EmitAsync(AgentEventKinds.Phase, new JsonObject { ["phase"] = phase });

    /// <summary>Streams a chunk of model thinking/narration text.</summary>
    public Task ThinkingAsync(string text)
        => string.IsNullOrWhiteSpace(text)
            ? Task.CompletedTask
            : EmitAsync(AgentEventKinds.Thinking, new JsonObject { ["text"] = text });

    /// <summary>Emits a tool_call chip as the kernel is about to invoke {module}.{function}(args).</summary>
    public Task ToolCallAsync(string module, string function, JsonObject? args)
        => EmitAsync(AgentEventKinds.ToolCall, new JsonObject
        {
            ["module"] = module,
            ["function"] = function,
            ["args"] = args?.DeepClone()
        });

    /// <summary>Emits a tool_result chip with a short human summary of what came back.</summary>
    public Task ToolResultAsync(string module, string function, string summary)
        => EmitAsync(AgentEventKinds.ToolResult, new JsonObject
        {
            ["module"] = module,
            ["function"] = function,
            ["summary"] = summary.Length <= 800 ? summary : summary[..800]
        });

    /// <summary>
    /// Emits a task_update as one task changes state, with the goal-level rollup.
    ///
    /// <para>
    /// The task DAG lives HERE — only the device can ground a decomposition — so this
    /// is how the cloud (and therefore Agent Board) learns what a goal is made of and
    /// how far along it is. progress/pending/next_step are DERIVED from task state,
    /// never from the clock: that is the whole reason the Task Manager exists.
    /// </para>
    /// </summary>
    public Task TaskUpdateAsync(TaskRecord task, int progressPercent, int pendingTasks, string? nextStep)
        => EmitAsync(AgentEventKinds.TaskUpdate, new JsonObject
        {
            ["task_id"] = task.TaskId,
            ["title"] = task.Title,
            ["state"] = ToWire(task.State),
            ["depends_on"] = new JsonArray(task.DependsOn.Select(d => JsonValue.Create(d)).ToArray<JsonNode?>()),
            ["progress_pct"] = progressPercent,
            ["pending_tasks"] = pendingTasks,
            ["next_step"] = nextStep,
            ["retry_count"] = task.RetryCount,
            ["failure_reason"] = task.FailureReason
        });

    /// <summary>
    /// PascalCase enum -> snake_case wire value ("AwaitingApproval" -> "awaiting_approval").
    ///
    /// <para>
    /// ToString().ToLowerInvariant() shipped "awaitingapproval", while the very same
    /// concept goes out as "awaiting_approval" in `task_status` and `phase` — one wire,
    /// two spellings of one idea. It hid because AwaitingApproval is the only multi-word
    /// member of TaskState, so every other value round-tripped correctly by accident,
    /// and a cloud check for a single-word state ("completed") passed on luck. Doing the
    /// conversion here keeps it right for whatever members get added later.
    /// </para>
    /// </summary>
    internal static string ToWire(TaskState state)
    {
        var name = state.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>Emits plan_progress as each plan item materializes.</summary>
    public Task PlanProgressAsync(PlanItem item)
        => EmitAsync(AgentEventKinds.PlanProgress, new JsonObject
        {
            ["item"] = JsonSerializer.SerializeToNode(item, ContractJson.Options)
        });

    private async Task EmitAsync(string kind, JsonObject payload)
    {
        var scope = Current.Value
            ?? throw new InvalidOperationException("Trace scope has not been started.");

        var evt = new AgentEvent
        {
            GoalId = scope.GoalId,
            CorrelationId = scope.CorrelationId,
            // Monotonic PER GOAL — the UI drops any frame whose seq isn't greater
            // than the last it saw for that goal.
            Seq = Interlocked.Increment(ref scope.Seq),
            Event = kind,
            Payload = payload
        };
        _logger.LogInformation("agent_event {Event} seq={Seq} payload={Payload}", kind, evt.Seq, payload.ToJsonString(ContractJson.Options));
        // Best-effort: a dropped/failed trace frame must NEVER crash planning.
        // The structured log above is the durable record; the streamed frame is
        // a live nicety.
        try
        {
            await _emit(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "agent_event emit failed (seq={Seq}); continuing", evt.Seq);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
