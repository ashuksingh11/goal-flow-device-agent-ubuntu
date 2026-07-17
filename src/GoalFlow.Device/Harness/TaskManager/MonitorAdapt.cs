using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace GoalFlow.Device.Harness;

/// <summary>
/// HARNESS MODULE: Monitor &amp; Adapt.
/// After a plan is approved the world keeps moving (a day passes, an item expires
/// early, a guest RSVPs an allergy). This turns "something changed" into "one
/// scoped re-plan, once" — which is what makes a one-shot plan into a living one
/// without nagging.
///
/// <para>
/// It knows NOTHING about the product. Domain observers
/// (<see cref="IDomainObserver"/>, contributed by the product pack) do the
/// watching and decide what is material; this module owns the guarantees around
/// them: capture a snapshot, ask only the observers that watch this goal's
/// domain, and turn each material change into a tiered adaptation proposal.
/// Until v3-M2 all of it lived here — a switch on "meal_plan"/"guest_dinner",
/// reads of "guests.pending_updates", a hardcoded prep window, guest copy.
/// </para>
/// </summary>
public sealed class MonitorAdapt
{
    private readonly IClock _clock;
    private readonly IReadOnlyList<IDomainObserver> _observers;
    private readonly ApprovalCoordinator _approvals;
    private readonly ILogger<MonitorAdapt> _logger;

    public MonitorAdapt(IClock clock, IEnumerable<IDomainObserver> observers, ApprovalCoordinator approvals, ILogger<MonitorAdapt> logger)
    {
        _clock = clock;
        _observers = observers.ToArray();
        _approvals = approvals;
        _logger = logger;
    }

    /// <summary>The observers watching a domain (none = nothing to sustain, which is fine).</summary>
    private IEnumerable<IDomainObserver> For(string domain)
        => _observers.Where(o => string.Equals(o.Domain, domain, StringComparison.Ordinal));

    /// <summary>
    /// The world as the observers see it now, merged into one snapshot and stamped
    /// with the clock's date. Every observer contributes its own slice — this
    /// module never names a document.
    /// </summary>
    public async Task<JsonObject> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = new JsonObject { ["captured_on"] = _clock.Today.ToString("yyyy-MM-dd") };
        foreach (var observer in _observers)
        {
            foreach (var (key, value) in await observer.CaptureAsync(ct))
            {
                snapshot[key] = value?.DeepClone();
            }
        }

        return snapshot;
    }

    /// <summary>Changes for this goal, as classified by the observers of its domain.</summary>
    public Task<IReadOnlyList<WorldChange>> ObserveAsync(GoalRecord goal, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorldChange>>(
            For(goal.Dispatch.Domain).SelectMany(o => o.Observe(goal)).ToArray());

    /// <summary>The fire-able event catalog advertised for a domain, if it has one.</summary>
    public IReadOnlyList<DemoEvent>? DemoEventsFor(string domain, JsonObject snapshot)
        => For(domain).Select(o => o.DemoEvents(snapshot)).FirstOrDefault(c => c is { Count: > 0 });

    /// <summary>Fire one catalog event on demand (control: trigger_event).</summary>
    public WorldChange? TriggerEvent(GoalRecord goal, string eventId)
        => For(goal.Dispatch.Domain).Select(o => o.TriggerEvent(goal, eventId)).FirstOrDefault(c => c is not null);

    /// <summary>
    /// For a material change: build the adaptation proposal (tier "adapt" =
    /// requires approval) — a scoped re-plan, e.g. "swap Thursday's dinner;
    /// spinach expired". Non-material changes yield null (logged only), which is
    /// the whole point of the materiality gate: four quiet days, one smart change.
    /// </summary>
    public Task<Proposal?> ProposeAdaptationAsync(string goalId, WorldChange change, CancellationToken ct = default)
    {
        if (!change.Material)
        {
            return Task.FromResult<Proposal?>(null);
        }

        var proposalId = $"a{_approvals.All().Count(p => p.ProposalId.StartsWith('a')) + 1}";
        var effect = BuildEffect(proposalId, change);
        if (effect is null)
        {
            // The observer marked this material but gave neither a Steer (→ a scoped
            // LLM re-plan) nor an effect to perform, so there is nothing to propose.
            // That is an observer bug, not a user-facing state — surfaced rather than
            // papered over with a guessed action, which is what the harness used to
            // do (it defaulted to Reminders.Create — a product module the generic
            // core had no business naming).
            _logger.LogWarning(
                "adaptation_underspecified {Key} kind={Kind}: material change has no Steer and no Effect — its observer must declare one",
                change.Key, change.Kind);
            return Task.FromResult<Proposal?>(null);
        }

        _approvals.Register(effect);

        var proposal = new Proposal
        {
            GoalId = goalId,
            TaskStatus = TaskStatuses.Adapting,
            Payload = new AdaptationPayload
            {
                ProposalId = proposalId,
                Action = effect.Action,
                Detail = effect.Reason,
                Trigger = change.Description,
                Tier = ApprovalTiers.Adapt,
                RequiresApproval = true
            }
        };

        _logger.LogInformation("adaptation_proposed {ProposalId} kind={Kind} trigger={Trigger}", proposalId, change.Kind, change.Description);
        return Task.FromResult<Proposal?>(proposal);
    }

    /// <summary>
    /// The effect an adaptation performs, or null when the change declares none.
    /// The OBSERVER chooses it — which module, which function, which arguments is
    /// product knowledge, and this module must not guess.
    /// </summary>
    private static ProposalItem? BuildEffect(string proposalId, WorldChange change)
        => change.EffectModule is null || change.EffectFunction is null
            ? null
            : new ProposalItem
            {
                ProposalId = proposalId,
                Action = change.EffectAction ?? change.RecommendedAction ?? "adapt plan",
                Module = change.EffectModule,
                Function = change.EffectFunction,
                Args = change.EffectArgs?.DeepClone().AsObject(),
                Tier = ApprovalTiers.Adapt,
                Reason = $"{change.RecommendedAction} Trigger: {change.Description}",
                RequiresApproval = true
            };
}

/// <summary>One observed world-state delta, as reported by a domain observer.</summary>
public sealed record WorldChange
{
    /// <summary>
    /// STABLE identity for dedup — the harness surfaces each key exactly once per
    /// goal. It must NOT embed today's date: observers use reach-or-pass triggers,
    /// so a day-stamped key would re-propose the same change every day.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>E.g. "inventory.expired", "calendar.event_overlap", "guest.rsvp_allergy_added".</summary>
    public required string Kind { get; init; }

    /// <summary>Human-readable description — becomes the proposal's trigger.</summary>
    public required string Description { get; init; }

    /// <summary>Plan-item ids this change touches (the re-plan slice).</summary>
    public IReadOnlyList<string> AffectedPlanItems { get; init; } = [];

    /// <summary>Stable plan-day index targeted by the change.</summary>
    public int? TargetDay { get; init; }

    /// <summary>Exact plan item id targeted.</summary>
    public string? TargetItemId { get; init; }

    /// <summary>Current title of the targeted plan item when the change was built.</summary>
    public string? TargetTitle { get; init; }

    /// <summary>
    /// Whether this is worth waking the user for. SET BY THE OBSERVER: only the
    /// domain knows that a nut allergy matters and a restocked pantry item nobody
    /// cooks with does not.
    /// </summary>
    public bool Material { get; init; }

    public string? RecommendedAction { get; init; }

    public string? EffectModule { get; init; }

    public string? EffectFunction { get; init; }

    public JsonObject? EffectArgs { get; init; }

    public string? EffectAction { get; init; }

    /// <summary>Extra facts handed to the scoped re-plan.</summary>
    public JsonObject? Context { get; init; }

    /// <summary>How the observer suggests the re-plan should lean.</summary>
    public string? Steer { get; init; }
}

