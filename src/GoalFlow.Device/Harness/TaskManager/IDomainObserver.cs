using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harness;

/// <summary>
/// Watches ONE domain's slice of the world and reports what changed.
///
/// <para>
/// THE SEAM: a plan is only worth sustaining if someone notices the world moved,
/// and noticing is irreducibly product knowledge — which documents exist, what
/// shape they are, and which of their changes are worth waking a family for. The
/// harness owns the GUARANTEE (only material changes surface, exactly once,
/// each becoming a scoped re-plan); the product owns the JUDGEMENT.
/// </para>
///
/// <para>
/// Before v3-M2 this all lived in <see cref="MonitorAdapt"/>: a switch on
/// "meal_plan"/"guest_dinner", reads of "guests.pending_updates" and
/// "daily_events.events", a hardcoded 17:30–18:30 prep window, and guest
/// adaptation copy. The generic core knew a fridge's data model by heart.
/// </para>
///
/// <para>
/// MATERIALITY IS THE OBSERVER'S CALL. <see cref="Observe"/> returns changes
/// with <see cref="WorldChange.Material"/> already set, because only the domain
/// knows that a guest's nut allergy matters and a restocked pantry item nobody
/// cooks with does not. This does not weaken "LLM plans, code checks": an
/// observer is deterministic code, never a model.
/// </para>
/// </summary>
public interface IDomainObserver
{
    /// <summary>The dispatch domain this observer watches (e.g. "meal_plan").</summary>
    string Domain { get; }

    /// <summary>
    /// This domain's slice of the world, as named documents merged into the goal's
    /// snapshot. The observer decides what it needs to watch; the harness never
    /// names a document.
    /// </summary>
    Task<JsonObject> CaptureAsync(CancellationToken ct = default);

    /// <summary>
    /// Changes since the snapshot, each already classified via
    /// <see cref="WorldChange.Material"/>. Deduplication is the harness's job —
    /// return what you see and use a STABLE <see cref="WorldChange.Key"/>.
    /// </summary>
    IReadOnlyList<WorldChange> Observe(ActiveGoalContext goal);

    /// <summary>
    /// The presenter's fire-able event catalog for this domain, or null if it has
    /// none. Advertised on plan_ready so the UI can offer the chips.
    /// </summary>
    IReadOnlyList<DemoEvent>? DemoEvents(JsonObject snapshot) => null;

    /// <summary>
    /// Fire one catalog event on demand (control: trigger_event), or null if this
    /// domain doesn't know that id. Lets the presenter force a change instead of
    /// waiting for the clock.
    /// </summary>
    WorldChange? TriggerEvent(ActiveGoalContext goal, string eventId) => null;
}
