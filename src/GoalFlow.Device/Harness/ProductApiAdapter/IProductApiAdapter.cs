using System.Text.Json.Nodes;

namespace GoalFlow.Device.Harness;

/// <summary>
/// HARNESS COMPONENT 5: Product API Adapter — THE PRODUCT SEAM.
///
/// Everything the capability plugins (and the harness's reset path) may touch of
/// the product's world, and nothing more. The harness talks to the product only
/// through this interface, so the core stays product-agnostic: swapping the
/// Family Hub mock for real Tizen/SmartThings/Food-AI calls means writing one
/// new implementation — no plugin, planner, or filter changes.
///
/// This makes the CODE_GUIDE's long-standing promise ("the Tizen port swaps
/// plugin internals, never the agent") a compile-time fact instead of a
/// convention.
///
/// <para>
/// GENERIC-CLOCK RULE (an invariant of this seam, not of any one implementation):
/// documents store dates as DAY OFFSETS relative to "today"
/// (<c>expires_in_days: 2</c>, <c>day_offset: 3</c>) and NEVER absolute dates.
/// <see cref="LoadResolvedAsync"/> resolves them against <see cref="Clock"/> at
/// READ time, so the world is always anchored to the real current date (or a
/// simulated one under control set_date / advance_day). Writes preserve offsets.
/// </para>
///
/// The surface is deliberately minimal — it is exactly what consumers call
/// today. Notably absent: the offset→ISO direction (ResolveOffset), which is
/// only ever used *inside* an implementation while resolving a document; adding
/// it here would be speculative.
/// </summary>
public interface IProductApiAdapter
{
    /// <summary>The generic clock every offset resolution reads (real or simulated).</summary>
    IClock Clock { get; }

    /// <summary>
    /// Loads the named document with relative dates RESOLVED: every
    /// <c>*_in_days</c> / <c>*_offset</c> integer gains a sibling ISO field
    /// (<c>expires_in_days: 2</c> → <c>expires_on: "&lt;today+2&gt;"</c>).
    /// </summary>
    Task<JsonObject> LoadResolvedAsync(string name, CancellationToken ct = default);

    /// <summary>Persists a mutated document (offsets preserved, never absolute dates).</summary>
    Task SaveAsync(string name, JsonObject document, CancellationToken ct = default);

    /// <summary>ISO date → offset from the clock's today. The inverse a writer needs to store an offset.</summary>
    int OffsetFromToday(string isoDate);

    /// <summary>Restores the pristine world (control: reset).</summary>
    Task ResetAsync(CancellationToken ct = default);
}
