using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace GoalFlow.Device.Contracts;

/// <summary>
/// Shared JSON serialization defaults for CONTRACT v0 messages.
/// All property names are pinned with <see cref="JsonPropertyNameAttribute"/>
/// (snake_case), so no naming policy is applied here.
/// The canonical CONTRACT.md lives in the cloud repo; this namespace is the
/// C# mirror and must track it exactly.
/// </summary>
public static class ContractJson
{
    /// <summary>Options used for every contract message read/written by the device.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
}

/// <summary>
/// The <c>type</c> discriminator values. All messages are JSON; <c>type</c>
/// discriminates. Every task message carries <c>goal_id</c>; device↔cloud
/// messages carry <c>correlation_id</c> (dedupe key; correlates
/// approval → proposal).
/// </summary>
public static class MessageTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "hello_ack";
    public const string Dispatch = "dispatch";
    public const string PlanReady = "plan_ready";
    public const string Proposal = "proposal";
    public const string Approval = "approval";
    public const string Status = "status";
}

/// <summary>
/// Task-status lifecycle:
/// created → planning → awaiting_approval → executing → adapting → done.
/// Modeled as string constants (not an enum) so the wire format stays exact.
/// </summary>
public static class TaskStatuses
{
    public const string Created = "created";
    public const string Planning = "planning";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Executing = "executing";
    public const string Adapting = "adapting";
    public const string Done = "done";
}

/// <summary>Autonomy modes carried on the Task Contract.</summary>
public static class AutonomyModes
{
    /// <summary>Every side-effect is frozen into a proposal and requires approval.</summary>
    public const string ProposeAll = "propose_all";
}
