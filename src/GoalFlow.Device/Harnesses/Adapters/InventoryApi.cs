namespace GoalFlow.Device.Harnesses.Adapters;

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

    public Task<IReadOnlyList<InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken = default) =>
        // TODO: deserialize data/inventory.json ("items" array) via ContractJson.Options.
        throw new NotImplementedException("Design stub.");
}
