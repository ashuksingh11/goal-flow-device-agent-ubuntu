namespace GoalFlow.Device.Harnesses.Adapters;

using GoalFlow.Device.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public async Task<IReadOnlyList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(cancellationToken);
        return data.Reminders;
    }

    public async Task CreateReminderAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(cancellationToken);
        if (reminder.CorrelationId is not null &&
            data.Reminders.Any(item => string.Equals(item.CorrelationId, reminder.CorrelationId, StringComparison.Ordinal)))
        {
            return;
        }

        await WriteAsync(data with { Reminders = data.Reminders.Concat([reminder]).ToArray() }, cancellationToken);
    }

    private async Task<ReminderFile> ReadAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_dataPath);
        return await JsonSerializer.DeserializeAsync<ReminderFile>(stream, ContractJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"Unable to deserialize reminder data '{_dataPath}'.");
    }

    private async Task WriteAsync(ReminderFile data, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_dataPath);
        await JsonSerializer.SerializeAsync(stream, data, ContractJson.Options, cancellationToken);
    }

    private sealed record ReminderFile
    {
        [JsonPropertyName("as_of")]
        public string? AsOf { get; init; }

        [JsonPropertyName("reminders")]
        public IReadOnlyList<Reminder> Reminders { get; init; } = [];
    }
}
