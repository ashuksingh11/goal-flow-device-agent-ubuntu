namespace GoalFlow.Device.Harnesses.Adapters;

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

    public Task<IReadOnlyList<ShoppingListEntry>> GetListAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Design stub.");

    public Task AddItemsAsync(
        IReadOnlyList<string> items,
        string? reason,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        // TODO: skip items whose correlation_id already exists (idempotent), then rewrite the file.
        throw new NotImplementedException("Design stub.");
}
