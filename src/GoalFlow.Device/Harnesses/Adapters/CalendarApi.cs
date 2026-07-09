namespace GoalFlow.Device.Harnesses.Adapters;

using GoalFlow.Device.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Product API adapter: family calendar (read-only in the POC).
/// Build effort: REAL INTERFACE + MOCK DATA.
/// </summary>
public interface ICalendarApi
{
    /// <summary>Events with a date inside [start, end] (inclusive).</summary>
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default);
}

/// <summary>Mock adapter backed by data/calendar.json.</summary>
public sealed class MockCalendarApi : ICalendarApi
{
    private readonly string _dataPath;

    /// <param name="dataPath">Path to calendar.json.</param>
    public MockCalendarApi(string dataPath) => _dataPath = dataPath;

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(_dataPath);
        var data = await JsonSerializer.DeserializeAsync<CalendarFile>(stream, ContractJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"Unable to deserialize calendar data '{_dataPath}'.");

        return data.Events
            .Where(evt => DateOnly.TryParse(evt.Date, out var date) && date >= start && date <= end)
            .OrderBy(evt => evt.Date, StringComparer.Ordinal)
            .ThenBy(evt => evt.Start, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record CalendarFile
    {
        [JsonPropertyName("events")]
        public IReadOnlyList<CalendarEvent> Events { get; init; } = [];
    }
}
