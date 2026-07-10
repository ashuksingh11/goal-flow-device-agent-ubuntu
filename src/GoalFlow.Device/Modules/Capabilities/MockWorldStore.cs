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
    private readonly Dictionary<string, string> _seed = [];

    public MockWorldStore(string dataDir, IClock clock)
    {
        _dataDir = dataDir;
        _clock = clock;
        Directory.CreateDirectory(_dataDir);
        foreach (var file in Directory.EnumerateFiles(_dataDir, "*.json"))
        {
            _seed[Path.GetFileName(file)] = File.ReadAllText(file);
        }
    }

    /// <summary>The clock all offset resolution uses (exposed for plugins).</summary>
    public IClock Clock => _clock;

    /// <summary>
    /// Loads <c>data/{name}.json</c> and RESOLVES relative dates: every
    /// <c>*_in_days</c> / <c>*_offset</c> integer field gains a sibling ISO
    /// field computed as <c>clock.Today + offset</c> (e.g. expires_in_days: 2
    /// → expires_on: "&lt;today+2&gt;").
    /// </summary>
    public async Task<JsonObject> LoadResolvedAsync(string name, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(PathFor(name), ct);
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException($"data/{name}.json is not a JSON object.");
        ResolveNode(root);
        return root;
    }

    /// <summary>Persists a mutated document back to <c>data/{name}.json</c> (offsets preserved).</summary>
    public Task SaveAsync(string name, JsonObject document, CancellationToken ct = default)
        => File.WriteAllTextAsync(PathFor(name), document.ToJsonString(new() { WriteIndented = true }), ct);

    /// <summary>Restores every data file from its pristine seed (control: reset).</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        foreach (var (file, contents) in _seed)
        {
            await File.WriteAllTextAsync(Path.Combine(_dataDir, file), contents, ct);
        }
    }

    /// <summary>Resolves one offset to an ISO date string against the generic clock.</summary>
    public string ResolveOffset(int dayOffset)
        => _clock.Today.AddDays(dayOffset).ToString("yyyy-MM-dd");

    public int OffsetFromToday(string isoDate)
        => DateOnly.Parse(isoDate).DayNumber - _clock.Today.DayNumber;

    private string PathFor(string name)
    {
        var fileName = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.json";
        return Path.Combine(_dataDir, fileName);
    }

    private void ResolveNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var additions = new List<(string Key, JsonNode? Value)>();
            foreach (var (key, value) in obj.ToArray())
            {
                ResolveNode(value);
                if (value is null || value.GetValueKind() == System.Text.Json.JsonValueKind.Null)
                {
                    continue;
                }

                if (key.EndsWith("_in_days", StringComparison.Ordinal) && value is JsonValue)
                {
                    var baseName = key[..^"_in_days".Length];
                    additions.Add(($"{baseName}_on", ResolveOffset(value.GetValue<int>())));
                }
                else if (key == "day_offset" && value is JsonValue)
                {
                    additions.Add(("date", ResolveOffset(value.GetValue<int>())));
                }
                else if (key.EndsWith("_offset", StringComparison.Ordinal) && value is JsonValue)
                {
                    var baseName = key[..^"_offset".Length];
                    additions.Add(($"{baseName}_date", ResolveOffset(value.GetValue<int>())));
                }
            }

            foreach (var (key, value) in additions)
            {
                obj[key] = value;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr)
            {
                ResolveNode(child);
            }
        }
    }
}
