namespace GoalFlow.Device.Harnesses.Adapters;

/// <summary>
/// Product API adapter: reminders — an ACTUATOR. Writes happen ONLY via the
/// EffectExecutor after an approval. Reminders fire off the virtual clock
/// (Scheduler), never a wall-clock timer.
/// Build effort: REAL INTERFACE + MOCK DATA.
/// </summary>
public interface IReminderApi
{
    /// <summary>Current reminders.</summary>
    Task<IReadOnlyList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates an approved reminder (carries its approval correlation_id).</summary>
    Task CreateReminderAsync(Reminder reminder, CancellationToken cancellationToken = default);
}

/// <summary>Mock adapter backed by data/reminders.json (read + rewrite file).</summary>
public sealed class MockReminderApi : IReminderApi
{
    private readonly string _dataPath;

    /// <param name="dataPath">Path to reminders.json.</param>
    public MockReminderApi(string dataPath) => _dataPath = dataPath;

    public Task<IReadOnlyList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Design stub.");

    public Task CreateReminderAsync(Reminder reminder, CancellationToken cancellationToken = default) =>
        // TODO: dedupe on reminder.CorrelationId (idempotent), then rewrite the file.
        throw new NotImplementedException("Design stub.");
}
