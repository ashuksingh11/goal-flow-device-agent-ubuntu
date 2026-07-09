namespace GoalFlow.Device.Harnesses.Adapters;

using GoalFlow.Device.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Product API adapter: shopping list — an ACTUATOR. Writes happen ONLY via
/// the EffectExecutor after an approval; nothing else calls
/// <see cref="AddItemsAsync"/>. Build effort: REAL INTERFACE + MOCK DATA.
/// </summary>
public interface IShoppingListApi
{
    /// <summary>Current list contents.</summary>
    Task<IReadOnlyList<ShoppingListEntry>> GetListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends approved items. <paramref name="correlationId"/> is persisted
    /// per entry so replays are detectable (idempotency).
    /// </summary>
    Task AddItemsAsync(
        IReadOnlyList<string> items,
        string? reason,
        string correlationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Mock adapter backed by data/shopping_list.json (read + rewrite file).</summary>
public sealed class MockShoppingListApi : IShoppingListApi
{
    private readonly string _dataPath;

    /// <param name="dataPath">Path to shopping_list.json.</param>
    public MockShoppingListApi(string dataPath) => _dataPath = dataPath;

    public async Task<IReadOnlyList<ShoppingListEntry>> GetListAsync(CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(cancellationToken);
        return data.Items;
    }

    public async Task AddItemsAsync(
        IReadOnlyList<string> items,
        string? reason,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(cancellationToken);
        if (data.Items.Any(item => string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal)))
        {
            return;
        }

        var existing = data.Items.ToList();
        var next = existing.Count + 1;
        existing.AddRange(items.Select(item => new ShoppingListEntry
        {
            Id = $"shop-{next++:000}",
            Item = item,
            Reason = reason,
            CorrelationId = correlationId,
        }));

        await WriteAsync(data with { Items = existing }, cancellationToken);
    }

    private async Task<ShoppingListFile> ReadAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_dataPath);
        return await JsonSerializer.DeserializeAsync<ShoppingListFile>(stream, ContractJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"Unable to deserialize shopping-list data '{_dataPath}'.");
    }

    private async Task WriteAsync(ShoppingListFile data, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_dataPath);
        await JsonSerializer.SerializeAsync(stream, data, ContractJson.Options, cancellationToken);
    }

    private sealed record ShoppingListFile
    {
        [JsonPropertyName("as_of")]
        public string? AsOf { get; init; }

        [JsonPropertyName("items")]
        public IReadOnlyList<ShoppingListEntry> Items { get; init; } = [];
    }
}
