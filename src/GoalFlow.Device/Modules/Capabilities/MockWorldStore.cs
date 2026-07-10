using System.Text.Json.Nodes;
using GoalFlow.Device.Modules.Steering;

namespace GoalFlow.Device.Modules.Capabilities;

/// <summary>
/// Shared mock-world access for the capability plugins (the Family Hub
/// "sensors/actuators" are JSON files under <c>data/</c> during development).
/// <para>
/// GENERIC-CLOCK RULE: the JSON files store dates as DAY OFFSETS relative to
/// "today" (e.g. <c>"expires_in_days": 2</c>, <c>"day_offset": 3</c>), never
/// absolute dates. This store resolves offsets against <see cref="IClock.Today"/>
/// at read time — so the mock world is always anchored to the REAL current
/// date (or the simulated date under control set_date / advance_day), and no
/// date is ever hardcoded anywhere.
/// </para>
/// Plugins go through this store; writes (shopping list adds, reminders,
/// consumed inventory) land back in the JSON files so state survives a run.
/// <c>control: reset</c> restores the pristine seed copies.
/// </summary>
public sealed class MockWorldStore
{
    private readonly string _dataDir;
    private readonly IClock _clock;

    public MockWorldStore(string dataDir, IClock clock)
    {
        _dataDir = dataDir;
        _clock = clock;
    }

    /// <summary>The clock all offset resolution uses (exposed for plugins).</summary>
    public IClock Clock => _clock;

    /// <summary>
    /// Loads <c>data/{name}.json</c> and RESOLVES relative dates: every
    /// <c>*_in_days</c> / <c>*_offset</c> integer field gains a sibling ISO
    /// field computed as <c>clock.Today + offset</c> (e.g. expires_in_days: 2
    /// → expires_on: "&lt;today+2&gt;").
    /// </summary>
    public Task<JsonObject> LoadResolvedAsync(string name, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Persists a mutated document back to <c>data/{name}.json</c> (offsets preserved).</summary>
    public Task SaveAsync(string name, JsonObject document, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Restores every data file from its pristine seed (control: reset).</summary>
    public Task ResetAsync(CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>Resolves one offset to an ISO date string against the generic clock.</summary>
    public string ResolveOffset(int dayOffset)
        => _clock.Today.AddDays(dayOffset).ToString("yyyy-MM-dd");
}
