using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;

namespace GoalFlow.Device.Harness;

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
    {
        _ledger[proposal.ProposalId] = (proposal, ProposalState.Pending);
        _logger.LogInformation("proposal_registered {ProposalId} {Module}.{Function} tier={Tier}", proposal.ProposalId, proposal.Module, proposal.Function, proposal.Tier);
        return proposal;
    }

    /// <summary>
    /// Applies an <c>approval</c> frame: each decision flips its proposal to
    /// Approved/Rejected. Returns the proposals now cleared for execution
    /// (the Actuator in GoalAgent invokes them and calls <see cref="MarkExecuted"/>).
    /// </summary>
    public IReadOnlyList<ProposalItem> ApplyDecisions(Approval approval)
    {
        var approved = new List<ProposalItem>();
        foreach (var decision in approval.Payload.Decisions)
        {
            if (!_ledger.TryGetValue(decision.ProposalId, out var entry))
            {
                _logger.LogWarning("approval_unknown_proposal {ProposalId}", decision.ProposalId);
                continue;
            }

            if (entry.State == ProposalState.Executed)
            {
                _logger.LogInformation("approval_replay_already_executed {ProposalId}", decision.ProposalId);
                continue;
            }

            var next = decision.Approved ? ProposalState.Approved : ProposalState.Rejected;
            _ledger[decision.ProposalId] = (entry.Item, next);
            _logger.LogInformation("approval_decision {ProposalId} approved={Approved}", decision.ProposalId, decision.Approved);
            if (next == ProposalState.Approved)
            {
                approved.Add(entry.Item);
            }
        }

        return approved;
    }

    /// <summary>Records idempotent execution: Approved → Executed exactly once.</summary>
    public void MarkExecuted(string proposalId)
    {
        if (!_ledger.TryGetValue(proposalId, out var entry))
        {
            return;
        }

        if (entry.State == ProposalState.Executed)
        {
            return;
        }

        _ledger[proposalId] = (entry.Item, ProposalState.Executed);
        _logger.LogInformation("proposal_executed {ProposalId}", proposalId);
    }

    /// <summary>All proposals for the plan_ready payload (any state).</summary>
    public IReadOnlyList<ProposalItem> All()
        => _ledger.Values.Select(v => v.Item).ToArray();

    /// <summary>Ids in Executed state — for the status frame's <c>executed</c> list.</summary>
    public IReadOnlyList<string> ExecutedIds()
        => _ledger.Where(kv => kv.Value.State == ProposalState.Executed).Select(kv => kv.Key).ToArray();
}
