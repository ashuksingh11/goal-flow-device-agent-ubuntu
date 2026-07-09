using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Act-phase harness: the ONLY component that touches actuators (shopping
/// list, reminders — via the product API adapters). Performs APPROVED
/// effects idempotently — dedupe on <c>correlation_id</c> + proposal_id, so a
/// replayed approval never double-writes — and records what was done.
/// Build effort: FULL logic later.
/// </summary>
public interface IEffectExecutor
{
    /// <summary>
    /// Executes one approved proposal by mapping its <c>action</c> to an
    /// adapter call ("add_to_shopping_list" → IShoppingListApi.AddItemsAsync,
    /// "add_prep_task" → IReminderApi.CreateReminderAsync, …). Idempotent.
    /// </summary>
    Task<EffectRecord> ExecuteAsync(PendingProposal approved, CancellationToken cancellationToken = default);

    /// <summary>Ledger of everything executed (feeds the trace/demo feed).</summary>
    IReadOnlyList<EffectRecord> History { get; }
}

/// <summary>Durable record of one executed (or skipped-as-duplicate) effect.</summary>
public sealed record EffectRecord
{
    public required string GoalId { get; init; }

    public required string ProposalId { get; init; }

    /// <summary>Idempotency key: correlation id of the approval round-trip.</summary>
    public required string CorrelationId { get; init; }

    public required string Action { get; init; }

    /// <summary>"executed" | "skipped_duplicate" | "failed".</summary>
    public required string Outcome { get; init; }

    /// <summary>Virtual-clock instant of execution.</summary>
    public required DateTimeOffset ExecutedAt { get; init; }
}

/// <summary>Skeleton implementation — full logic in the implementation phase.</summary>
public sealed class EffectExecutor : IEffectExecutor
{
    private readonly Adapters.IShoppingListApi _shoppingList;
    private readonly Adapters.IReminderApi _reminders;
    private readonly IClock _clock;
    private readonly ITrace _trace;

    public EffectExecutor(
        Adapters.IShoppingListApi shoppingList,
        Adapters.IReminderApi reminders,
        IClock clock,
        ITrace trace)
    {
        _shoppingList = shoppingList;
        _reminders = reminders;
        _clock = clock;
        _trace = trace;
    }

    public IReadOnlyList<EffectRecord> History =>
        throw new NotImplementedException("Design stub.");

    public Task<EffectRecord> ExecuteAsync(PendingProposal approved, CancellationToken cancellationToken = default) =>
        // TODO: refuse anything not in ApprovalState.Approved; dedupe on
        // (CorrelationId, ProposalId); dispatch on action; trace outcome.
        throw new NotImplementedException("Design stub.");
}
