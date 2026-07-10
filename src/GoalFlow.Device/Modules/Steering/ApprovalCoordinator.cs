using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;

namespace GoalFlow.Device.Modules.Steering;

/// <summary>
/// HARNESS MODULE: Approval / Consent (HITL) — the device half.
/// (The durable pause/resume lives cloud-side in LangGraph <c>interrupt()</c>;
/// here the coordinator owns the proposal LEDGER.) Side-effecting tool calls
/// the LLM proposes are frozen into <see cref="ProposalItem"/>s with a tier:
/// <c>auto</c> may execute immediately, <c>light</c> rides the plan approval,
/// <c>firm</c> (spends money / irreversible) NEVER executes until an explicit
/// approval decision arrives. Lifecycle per proposal:
/// pending → approved → executed (or rejected).
/// </summary>
public sealed class ApprovalCoordinator
{
    /// <summary>Ledger states for a tracked proposal.</summary>
    public enum ProposalState
    {
        Pending,
        Approved,
        Rejected,
        Executed,
    }

    private readonly ILogger<ApprovalCoordinator> _logger;
    private readonly Dictionary<string, (ProposalItem Item, ProposalState State)> _ledger = [];

    public ApprovalCoordinator(ILogger<ApprovalCoordinator> logger) => _logger = logger;

    /// <summary>
    /// Freezes a pending side-effecting call into the ledger. Called when the
    /// planner run surfaces a [SideEffect]-tagged function call instead of
    /// executing it. Returns the registered proposal (id assigned here).
    /// </summary>
    public ProposalItem Register(ProposalItem proposal)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Classifies a function's tier from its <see cref="SideEffectAttribute"/> metadata.</summary>
    public string TierOf(string module, string function)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>
    /// Applies an <c>approval</c> frame: each decision flips its proposal to
    /// Approved/Rejected. Returns the proposals now cleared for execution
    /// (the Actuator in GoalAgent invokes them and calls <see cref="MarkExecuted"/>).
    /// </summary>
    public IReadOnlyList<ProposalItem> ApplyDecisions(Approval approval)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Records idempotent execution: Approved → Executed exactly once.</summary>
    public void MarkExecuted(string proposalId)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>All proposals for the plan_ready payload (any state).</summary>
    public IReadOnlyList<ProposalItem> All()
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Ids in Executed state — for the status frame's <c>executed</c> list.</summary>
    public IReadOnlyList<string> ExecutedIds()
        => throw new NotImplementedException("v2-M0 design skeleton");
}
