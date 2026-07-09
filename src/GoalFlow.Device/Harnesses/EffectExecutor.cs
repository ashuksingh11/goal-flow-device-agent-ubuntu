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

    /// <summary>Wire-level result, e.g. "added" or "created".</summary>
    public required string Result { get; init; }

    /// <summary>Human-readable execution detail for status messages.</summary>
    public required string Detail { get; init; }

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
    private readonly List<EffectRecord> _history = [];
    private readonly HashSet<string> _executedKeys = new(StringComparer.Ordinal);

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

    public IReadOnlyList<EffectRecord> History => _history;

    public async Task<EffectRecord> ExecuteAsync(PendingProposal approved, CancellationToken cancellationToken = default)
    {
        if (approved.State != ApprovalState.Approved)
        {
            throw new InvalidOperationException($"Proposal {approved.Proposal.ProposalId} is {approved.State}, not approved.");
        }

        var key = ExecutionKey(approved);
        if (!_executedKeys.Add(key) || await AlreadyPersistedAsync(approved, key, cancellationToken))
        {
            return Record(approved, "skipped_duplicate", ResultFor(approved.Proposal.Action), DuplicateDetail(approved));
        }

        var proposal = approved.Proposal;
        var result = ResultFor(proposal.Action);
        var detail = proposal.Action switch
        {
            "add_to_shopping_list" => await AddShoppingItemsAsync(proposal, key, cancellationToken),
            "add_prep_task" or "add_reminder" or "create_reminder" => await CreateReminderAsync(approved, key, cancellationToken),
            _ when proposal.Action.Contains("reminder", StringComparison.OrdinalIgnoreCase) => await CreateReminderAsync(approved, key, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported proposal action '{proposal.Action}'."),
        };

        return Record(approved, "executed", result, detail);
    }

    private async Task<string> AddShoppingItemsAsync(
        ProposalItem proposal,
        string key,
        CancellationToken cancellationToken)
    {
        var items = proposal.Items ?? [];
        await _shoppingList.AddItemsAsync(items, proposal.Reason, key, cancellationToken);
        return $"{items.Count} items added to shopping list";
    }

    private async Task<string> CreateReminderAsync(
        PendingProposal approved,
        string key,
        CancellationToken cancellationToken)
    {
        var proposal = approved.Proposal;
        var reminder = new Reminder
        {
            Id = $"rem-{proposal.ProposalId}",
            Text = proposal.Detail ?? proposal.Reason ?? proposal.Action,
            Date = _clock.Today.ToString("yyyy-MM-dd"),
            Time = "09:00",
            CorrelationId = key,
        };

        await _reminders.CreateReminderAsync(reminder, cancellationToken);
        return "prep reminder created";
    }

    private async Task<bool> AlreadyPersistedAsync(
        PendingProposal approved,
        string key,
        CancellationToken cancellationToken)
    {
        if (approved.Proposal.Action == "add_to_shopping_list")
        {
            var items = await _shoppingList.GetListAsync(cancellationToken);
            return items.Any(item => string.Equals(item.CorrelationId, key, StringComparison.Ordinal));
        }

        var reminders = await _reminders.GetRemindersAsync(cancellationToken);
        return reminders.Any(item => string.Equals(item.CorrelationId, key, StringComparison.Ordinal));
    }

    private EffectRecord Record(PendingProposal approved, string outcome, string result, string detail)
    {
        var record = new EffectRecord
        {
            GoalId = approved.GoalId,
            ProposalId = approved.Proposal.ProposalId,
            CorrelationId = approved.CorrelationId,
            Action = approved.Proposal.Action,
            Outcome = outcome,
            Result = result,
            Detail = detail,
            ExecutedAt = _clock.Now,
        };

        _history.Add(record);
        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = approved.GoalId,
            Phase = TracePhase.Act,
            Source = nameof(EffectExecutor),
            Kind = outcome,
            Message = $"{approved.Proposal.ProposalId} {approved.Proposal.Action}: {detail}",
        });
        return record;
    }

    private static string ExecutionKey(PendingProposal approved) =>
        $"{approved.CorrelationId}:{approved.Proposal.ProposalId}";

    private static string ResultFor(string action) =>
        action == "add_to_shopping_list" ? "added" : "created";

    private static string DuplicateDetail(PendingProposal approved) =>
        $"{approved.Proposal.ProposalId} already applied for {approved.CorrelationId}";
}
