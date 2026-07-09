namespace GoalFlow.Device.Harnesses.Adapters;

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

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default) =>
        // TODO: deserialize data/calendar.json ("events" array) and filter by date range.
        throw new NotImplementedException("Design stub.");
}
