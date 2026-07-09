namespace GoalFlow.Device.Harnesses.Adapters;

using GoalFlow.Device.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public async Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(_dataPath);
        var data = await JsonSerializer.DeserializeAsync<RecipeFile>(stream, ContractJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"Unable to deserialize recipe data '{_dataPath}'.");
        return data.Recipes;
    }

    private sealed record RecipeFile
    {
        [JsonPropertyName("recipes")]
        public IReadOnlyList<Recipe> Recipes { get; init; } = [];
    }
}
