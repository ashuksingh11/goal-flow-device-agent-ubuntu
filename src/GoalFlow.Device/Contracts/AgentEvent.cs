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

    /// <summary>
    /// Payload: { "text": "...", "kind"?, "step"?, "detail"? } — model reasoning, or
    /// (v7) a labelled step of the work. See <see cref="ThinkingKinds"/>.
    /// </summary>
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

    /// <summary>
    /// Payload: { module, status, note?, verdict?, grade? } — one HARNESS ENGINE
    /// entered/finished a step (v5). Where <c>phase</c> is coarse (grounding →
    /// planning → checking), this names the specific engine doing the work —
    /// Pre-Check, Capability Manager, Grounding, Planner, Safety Policy, Task
    /// Manager, Approval, Monitor &amp; Adapt — so the UI can render the "harness
    /// pipeline" lighting up engine-by-engine. Additive: an unknown module/status
    /// is ignored rather than fatal.
    /// </summary>
    public const string Harness = "harness";
}

/// <summary>
/// The <c>module</c> values a <see cref="AgentEventKinds.Harness"/> event can carry —
/// one per harness engine, in roughly the order they fire during a plan. Unknown
/// values are ignored by the UI, so adding an engine is additive.
/// </summary>
public static class HarnessModules
{
    public const string Precheck = "precheck";
    public const string CapabilityManager = "capability_manager";
    public const string Grounding = "grounding";
    public const string Planner = "planner";
    public const string Safety = "safety";
    public const string TaskManager = "task_manager";
    public const string Approval = "approval";
    public const string MonitorAdapt = "monitor_adapt";
}

/// <summary>
/// The <c>status</c> values a <see cref="AgentEventKinds.Harness"/> event can carry.
/// <c>enter</c>/<c>active</c> light the engine up (the "now X is working" beat, held
/// by the demo dwell); <c>pass</c>/<c>done</c> resolve it green; <c>block</c> resolves
/// it red; <c>skip</c> greys it out (engine not needed this run).
/// </summary>
public static class HarnessStatuses
{
    public const string Enter = "enter";
    public const string Active = "active";
    public const string Pass = "pass";
    public const string Block = "block";
    public const string Done = "done";
    public const string Skip = "skip";
}

/// <summary>Typed payload helpers (serialized into <see cref="AgentEvent.Payload"/>).</summary>
public sealed record PhasePayload(string Phase);

/// <summary>Payload for a <see cref="AgentEventKinds.Harness"/> event.</summary>
public sealed record HarnessPayload(string Module, string Status, string? Note = null, string? Verdict = null, string? Grade = null);

/// <summary>
/// What a <see cref="AgentEventKinds.Thinking"/> event IS (v7). Additive: absent means
/// <see cref="Narration"/>, which is every thinking event emitted before v7.
/// </summary>
public static class ThinkingKinds
{
    /// <summary>Streamed model prose, arriving a fragment at a time. Merge on the client.</summary>
    public const string Narration = "narration";

    /// <summary>
    /// One labelled step of the work, whole and self-contained: <c>step</c> is the
    /// headline, <c>detail</c> the sub-line. Never fragmented, so a client renders it
    /// on arrival rather than accumulating it.
    /// </summary>
    public const string Step = "step";

    /// <summary>A retry, a fallback, an error — the run talking about itself.</summary>
    public const string Notice = "notice";
}

/// <summary>
/// Model reasoning, or a labelled step of the work.
///
/// <para>
/// v7 ADDED THE STRUCTURE, AND THE REASON IS THE PLANNER. Through v6 this was one
/// untyped string, which forced every client to guess: whether a fragment continued the
/// last one, whether a chunk was prose or the JSON the model interleaved with it (the
/// chat UI carries ~150 lines of heuristics for exactly that), and — worst — the
/// composing screen was simply BLANK during planning, because the compose call is not
/// streamed and deliberately keeps its plan JSON off this channel. A silent engine and a
/// broken one look identical.
/// </para>
///
/// <para>
/// <paramref name="Text"/> stays required and stays first: a client that ignores the new
/// fields renders exactly what it did before. For a step it holds "step — detail", so
/// even the unstructured reading is a sentence.
/// </para>
/// </summary>
public sealed record ThinkingPayload(string Text, string? Kind = null, string? Step = null, string? Detail = null);

public sealed record ToolCallPayload(string Module, string Function, JsonObject? Args);

public sealed record ToolResultPayload(string Module, string Function, string Summary);

/// <summary>
/// One plan item taking shape. <paramref name="Total"/> (v5.1) is how many items the
/// finished plan has: the compose call is NOT streamed, so every item is emitted in one
/// loop and a UI cannot otherwise tell how many are still coming. With it a surface can
/// reserve exactly N rows up front, whatever the goal's shape.
/// </summary>
public sealed record PlanProgressPayload(PlanItem Item, int Total);
