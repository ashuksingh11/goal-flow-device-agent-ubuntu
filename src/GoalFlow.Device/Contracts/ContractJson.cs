using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Shared System.Text.Json defaults for CONTRACT v2 messages.
/// All wire names are snake_case via <see cref="JsonNamingPolicy.SnakeCaseLower"/>;
/// C# properties stay PascalCase. The canonical CONTRACT v2 lives in the cloud
/// repo; this namespace is the C# mirror and must track it exactly.
/// </summary>
public static class ContractJson
{
    /// <summary>Options used for every contract message the device reads or writes.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null, // free-form objects (scope/context/args) keep caller keys verbatim
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false, // wire frames are compact; pretty-print at the edges if needed
    };

    /// <summary>Serialize a contract message to a wire frame.</summary>
    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Deserialize a wire frame; throws if the payload does not match.</summary>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"Frame deserialized to null for {typeof(T).Name}.");

    /// <summary>Peek the <c>type</c> discriminator of an incoming frame.</summary>
    public static string? PeekType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
    }
}

/// <summary>
/// The <c>type</c> discriminator values. Every frame is JSON with <c>type</c>;
/// task messages carry <c>goal_id</c>; device↔cloud messages carry
/// <c>correlation_id</c> (dedupe / correlation key).
/// </summary>
public static class MessageTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "hello_ack";
    public const string Capabilities = "capabilities";
    public const string UserGoal = "user_goal";
    public const string Dispatch = "dispatch";
    public const string AgentEvent = "agent_event";
    public const string PlanReady = "plan_ready";
    public const string PresentPlan = "present_plan";
    public const string Approval = "approval";
    public const string Proposal = "proposal";
    public const string Status = "status";
    public const string Control = "control";
    public const string Suggestions = "suggestions";
}

/// <summary>
/// Task-status lifecycle:
/// created → interpreting → grounding → planning → checking →
/// awaiting_approval → executing → monitoring → adapting → done.
/// String constants (not an enum) so the wire format stays exact.
/// </summary>
public static class TaskStatuses
{
    public const string Created = "created";
    public const string Interpreting = "interpreting";
    public const string Grounding = "grounding";
    public const string Planning = "planning";
    public const string Checking = "checking";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Executing = "executing";
    public const string Monitoring = "monitoring";
    public const string Adapting = "adapting";
    public const string Done = "done";
}

/// <summary>
/// What happened to an approved effect (<c>status.payload.executed[].result</c>).
/// </summary>
public static class ExecutionResults
{
    /// <summary>It ran.</summary>
    public const string Executed = "executed";

    /// <summary>
    /// It did NOT run: a pre-check failed at actuation time (the oven went offline
    /// between planning and approval). The approval still stands — re-applying it
    /// once the world recovers executes it. NOT a failure, and not a silent drop:
    /// v3-M3.
    /// </summary>
    public const string DeferredPrecheck = "deferred_precheck";
}

/// <summary>
/// Approval tiers for side-effecting actions (reversibility × cost × risk).
/// Nothing <see cref="Firm"/> executes until an <c>approval</c> arrives.
/// </summary>
public static class ApprovalTiers
{
    /// <summary>Reversible and cheap — the device may just do it (e.g. set a reminder).</summary>
    public const string Auto = "auto";

    /// <summary>Low-stakes; batched into the plan approval (e.g. add to shopping list).</summary>
    public const string Light = "light";

    /// <summary>Costly/irreversible; requires explicit consent (e.g. place a grocery order).</summary>
    public const string Firm = "firm";

    /// <summary>Material sustain-loop adaptation; always presented for explicit adapt approval.</summary>
    public const string Adapt = "adapt";
}
