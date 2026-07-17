using System.Text.Json.Nodes;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Live progress frame, device → cloud → ui (<c>type: "agent_event"</c>),
/// STREAMED while the device works. This is what drives the "watch it think"
/// UI: phase changes on the progress rail, tool-call chips as the LLM invokes
/// [KernelFunction]s, streamed thinking text, and per-item plan progress.
/// Emitted by <c>Modules.Steering.Trace</c>; <see cref="Seq"/> is a
/// monotonically increasing per-goal sequence for ordering/dedupe.
/// </summary>
public sealed record AgentEvent
{
    public string Type { get; init; } = MessageTypes.AgentEvent;

    public required string GoalId { get; init; }

    public string? CorrelationId { get; init; }

    public required int Seq { get; init; }

    /// <summary>One of <see cref="AgentEventKinds"/>.</summary>
    public required string Event { get; init; }

    /// <summary>Kind-shaped payload; see the payload records below for the shapes.</summary>
    public required JsonObject Payload { get; init; }
}

/// <summary>The <c>event</c> discriminator values.</summary>
/// <summary>
/// The <c>phase</c> values an agent_event can carry. The UI renders these as its
/// progress rail; an unknown one is ignored rather than fatal, so adding a phase
/// is additive.
/// </summary>
public static class Phases
{
    /// <summary>
    /// Another goal holds the single planning slot; this one starts next (v3-M5).
    /// It exists so a queued goal is VISIBLE — the board shows Waiting — rather
    /// than a card that sits doing nothing for a minute with no explanation.
    /// </summary>
    public const string Queued = "queued";

    public const string Grounding = "grounding";
    public const string Planning = "planning";
    public const string Checking = "checking";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Executing = "executing";
    public const string Monitoring = "monitoring";
    public const string Adapting = "adapting";
}

public static class AgentEventKinds
{
    /// <summary>Payload: { "phase": … } — see <see cref="Phases"/>.</summary>
    public const string Phase = "phase";

    /// <summary>Payload: { "text": "..." } — streamed model reasoning/narration.</summary>
    public const string Thinking = "thinking";

    /// <summary>Payload: { "module": "...", "function": "...", "args": {...} }.</summary>
    public const string ToolCall = "tool_call";

    /// <summary>Payload: { "module": "...", "function": "...", "summary": "..." }.</summary>
    public const string ToolResult = "tool_result";

    /// <summary>Payload: { "item": {...} } — a plan item just materialized.</summary>
    public const string PlanProgress = "plan_progress";

    /// <summary>
    /// Payload: { task_id, title, state, depends_on, progress_pct, pending_tasks,
    /// next_step, retry_count, failure_reason } — one task changed state (v3-M6).
    ///
    /// <para>
    /// The task DAG lives on the DEVICE (only it can ground a decomposition), so this
    /// is how the cloud learns what a goal is made of and how far along it is. Agent
    /// Board's progress %, next step and pending count are folded from these — derived
    /// from real task state rather than guessed from plan-day vs the clock.
    /// </para>
    /// </summary>
    public const string TaskUpdate = "task_update";
}

/// <summary>Typed payload helpers (serialized into <see cref="AgentEvent.Payload"/>).</summary>
public sealed record PhasePayload(string Phase);

public sealed record ThinkingPayload(string Text);

public sealed record ToolCallPayload(string Module, string Function, JsonObject? Args);

public sealed record ToolResultPayload(string Module, string Function, string Summary);

public sealed record PlanProgressPayload(PlanItem Item);
