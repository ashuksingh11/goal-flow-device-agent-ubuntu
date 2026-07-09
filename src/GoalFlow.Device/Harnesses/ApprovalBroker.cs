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

    public ApprovalBroker(IClock clock, ITrace trace)
    {
        _clock = clock;
        _trace = trace;
    }

    public void Submit(string goalId, string correlationId, IReadOnlyList<ProposalItem> proposals) =>
        throw new NotImplementedException("Design stub.");

    public IReadOnlyList<PendingProposal> ApplyDecisions(Approval approval) =>
        // TODO: match approval.CorrelationId + decision.ProposalId; dedupe
        // replays; trace each transition.
        throw new NotImplementedException("Design stub.");

    public PendingProposal? Find(string proposalId) =>
        throw new NotImplementedException("Design stub.");

    public IReadOnlyList<PendingProposal> PendingFor(string goalId) =>
        throw new NotImplementedException("Design stub.");

    public void ExpireOverdue() =>
        throw new NotImplementedException("Design stub.");
}
