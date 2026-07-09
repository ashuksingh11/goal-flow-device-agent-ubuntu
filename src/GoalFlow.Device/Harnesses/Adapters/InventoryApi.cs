namespace GoalFlow.Device.Harnesses.Adapters;

using GoalFlow.Device.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Product API adapter: fridge/pantry inventory (read-only in the POC).
/// Build effort: REAL INTERFACE + MOCK DATA. Tizen port swaps in a Family
/// Hub inventory adapter behind this same interface — no harness changes.
/// </summary>
public interface IInventoryApi
{
    /// <summary>Current inventory snapshot.</summary>
    Task<IReadOnlyList<InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Mock adapter backed by data/inventory.json.</summary>
public sealed class MockInventoryApi : IInventoryApi
{
    private readonly string _dataPath;

    /// <param name="dataPath">Path to inventory.json.</param>
    public MockInventoryApi(string dataPath) => _dataPath = dataPath;

    public async Task<IReadOnlyList<InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(_dataPath);
        var data = await JsonSerializer.DeserializeAsync<InventoryFile>(stream, ContractJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"Unable to deserialize inventory data '{_dataPath}'.");
        return data.Items;
    }

    private sealed record InventoryFile
    {
        [JsonPropertyName("items")]
        public IReadOnlyList<InventoryItem> Items { get; init; } = [];
    }
}
