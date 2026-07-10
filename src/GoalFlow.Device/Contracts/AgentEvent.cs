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
public static class AgentEventKinds
{
    /// <summary>Payload: { "phase": "grounding" | "planning" | "checking" | "awaiting_approval" }.</summary>
    public const string Phase = "phase";

    /// <summary>Payload: { "text": "..." } — streamed model reasoning/narration.</summary>
    public const string Thinking = "thinking";

    /// <summary>Payload: { "module": "...", "function": "...", "args": {...} }.</summary>
    public const string ToolCall = "tool_call";

    /// <summary>Payload: { "module": "...", "function": "...", "summary": "..." }.</summary>
    public const string ToolResult = "tool_result";

    /// <summary>Payload: { "item": {...} } — a plan item just materialized.</summary>
    public const string PlanProgress = "plan_progress";
}

/// <summary>Typed payload helpers (serialized into <see cref="AgentEvent.Payload"/>).</summary>
public sealed record PhasePayload(string Phase);

public sealed record ThinkingPayload(string Text);

public sealed record ToolCallPayload(string Module, string Function, JsonObject? Args);

public sealed record ToolResultPayload(string Module, string Function, string Summary);

public sealed record PlanProgressPayload(PlanItem Item);
