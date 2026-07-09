namespace GoalFlow.Device.Harnesses.Adapters;

/// <summary>
/// Product API adapter: recipe catalog (read-only in the POC).
/// Build effort: REAL INTERFACE + MOCK DATA.
/// </summary>
public interface IRecipeApi
{
    /// <summary>All recipes available to the planner.</summary>
    Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Mock adapter backed by data/recipes.json.</summary>
public sealed class MockRecipeApi : IRecipeApi
{
    private readonly string _dataPath;

    /// <param name="dataPath">Path to recipes.json.</param>
    public MockRecipeApi(string dataPath) => _dataPath = dataPath;

    public Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default) =>
        // TODO: deserialize data/recipes.json ("recipes" array).
        throw new NotImplementedException("Design stub.");
}
