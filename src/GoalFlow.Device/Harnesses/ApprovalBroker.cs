using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Gate-phase harness #2 of 2 — the APPROVAL gate.
/// <para>
/// The human is the gate; the cloud is the wire. This broker freezes
/// side-effects as proposals, tracks their state
/// (pending → approved / rejected / expired), and correlates each incoming
/// decision back to its proposal by <c>correlation_id</c> (idempotent —
/// duplicate approvals on reconnect are deduped). It WAITS; it never blocks
/// on its own judgment (that is the safety gate's job).
/// </para>
/// Build effort: FULL logic later.
/// </summary>
public interface IApprovalBroker
{
    /// <summary>
    /// Registers proposals sent to the cloud under one correlation id
    /// (the plan_ready's or the adaptation proposal's). State: Pending.
    /// </summary>
    void Submit(string goalId, string correlationId, IReadOnlyList<ProposalItem> proposals);

    /// <summary>
    /// Applies an approval message. Dedupes on correlation_id; returns the
    /// proposals that just transitioned to Approved (ready for the
    /// EffectExecutor). Unknown/duplicate decisions are ignored with a trace.
    /// </summary>
    IReadOnlyList<PendingProposal> ApplyDecisions(Approval approval);

    /// <summary>Current state of one proposal; null when unknown.</summary>
    PendingProposal? Find(string proposalId);

    /// <summary>Marks approved proposals as executed after effects complete.</summary>
    void MarkExecuted(IReadOnlyList<PendingProposal> executed);

    /// <summary>All proposals still awaiting a decision for the goal.</summary>
    IReadOnlyList<PendingProposal> PendingFor(string goalId);

    /// <summary>
    /// Expires proposals whose deadline (virtual clock) has passed.
    /// Invoked by the Scheduler (M4).
    /// </summary>
    void ExpireOverdue();
}

/// <summary>Broker-side lifecycle state of a proposal.</summary>
public enum ApprovalState
{
    Pending,
    Approved,
    Executed,
    Rejected,
    Expired,
}

/// <summary>A tracked proposal with its decision state.</summary>
public sealed record PendingProposal
{
    public required string GoalId { get; init; }

    /// <summary>Correlation id of the message that carried it (dedupe key).</summary>
    public required string CorrelationId { get; init; }

    public required ProposalItem Proposal { get; init; }

    public required ApprovalState State { get; init; }

    /// <summary>Virtual-clock instant it was submitted.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>Optional expiry deadline (virtual clock).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Skeleton implementation — full logic in the implementation phase.</summary>
public sealed class ApprovalBroker : IApprovalBroker
{
    private readonly IClock _clock;
    private readonly ITrace _trace;
    private readonly Dictionary<string, PendingProposal> _proposals = new(StringComparer.Ordinal);

    public ApprovalBroker(IClock clock, ITrace trace)
    {
        _clock = clock;
        _trace = trace;
    }

    public void Submit(string goalId, string correlationId, IReadOnlyList<ProposalItem> proposals)
    {
        foreach (var proposal in proposals)
        {
            var key = Key(correlationId, proposal.ProposalId);
            if (_proposals.ContainsKey(key))
            {
                continue;
            }

            _proposals[key] = new PendingProposal
            {
                GoalId = goalId,
                CorrelationId = correlationId,
                Proposal = proposal,
                State = ApprovalState.Pending,
                SubmittedAt = _clock.Now,
            };
        }

        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = goalId,
            Phase = TracePhase.Gate,
            Source = nameof(ApprovalBroker),
            Kind = "proposals_submitted",
            Message = $"{proposals.Count} proposals pending approval",
        });
    }

    public IReadOnlyList<PendingProposal> ApplyDecisions(Approval approval)
    {
        var approved = new List<PendingProposal>();
        foreach (var decision in approval.Payload.Decisions)
        {
            var key = Key(approval.CorrelationId, decision.ProposalId);
            if (!_proposals.TryGetValue(key, out var pending) || pending.State != ApprovalState.Pending)
            {
                TraceDecision(approval.GoalId, decision.ProposalId, "ignored");
                continue;
            }

            var next = pending with { State = decision.Approved ? ApprovalState.Approved : ApprovalState.Rejected };
            _proposals[key] = next;
            if (decision.Approved)
            {
                approved.Add(next);
            }

            TraceDecision(approval.GoalId, decision.ProposalId, next.State.ToString().ToLowerInvariant());
        }

        return approved;
    }

    public void MarkExecuted(IReadOnlyList<PendingProposal> executed)
    {
        foreach (var proposal in executed)
        {
            var key = Key(proposal.CorrelationId, proposal.Proposal.ProposalId);
            if (!_proposals.TryGetValue(key, out var pending) || pending.State != ApprovalState.Approved)
            {
                continue;
            }

            _proposals[key] = pending with { State = ApprovalState.Executed };
            TraceDecision(pending.GoalId, pending.Proposal.ProposalId, "executed");
        }
    }

    public PendingProposal? Find(string proposalId) =>
        _proposals.Values.FirstOrDefault(item =>
            string.Equals(item.Proposal.ProposalId, proposalId, StringComparison.Ordinal));

    public IReadOnlyList<PendingProposal> PendingFor(string goalId) =>
        _proposals.Values
            .Where(item => string.Equals(item.GoalId, goalId, StringComparison.Ordinal) && item.State == ApprovalState.Pending)
            .ToArray();

    public void ExpireOverdue()
    {
        foreach (var (key, proposal) in _proposals.ToArray())
        {
            if (proposal.State == ApprovalState.Pending &&
                proposal.ExpiresAt is not null &&
                proposal.ExpiresAt <= _clock.Now)
            {
                _proposals[key] = proposal with { State = ApprovalState.Expired };
                TraceDecision(proposal.GoalId, proposal.Proposal.ProposalId, "expired");
            }
        }
    }

    private void TraceDecision(string goalId, string proposalId, string state) =>
        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = goalId,
            Phase = TracePhase.Gate,
            Source = nameof(ApprovalBroker),
            Kind = "approval_state",
            Message = $"{proposalId} {state}",
        });

    private static string Key(string correlationId, string proposalId) => $"{correlationId}:{proposalId}";
}
