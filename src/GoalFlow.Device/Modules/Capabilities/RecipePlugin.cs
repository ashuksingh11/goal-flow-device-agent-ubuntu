using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Capabilities;

/// <summary>
/// CAPABILITY MODULE (meal domain): the recipe box (Samsung Food stand-in).
/// SK plugin, name "Recipes". Backed by data/recipes.json (no dates).
/// Read-only. The SafetyFilter screens recipe choices indirectly: side-effects
/// derived from a recipe (shopping adds, reminders) carry its ingredients.
/// </summary>
[Description("Recipe search and details: ingredients, allergen tags, prep time.")]
public sealed class RecipePlugin
{
    private readonly MockWorldStore _store;

    public RecipePlugin(MockWorldStore store) => _store = store;

    [KernelFunction]
    [Description("Finds recipes, optionally filtered by tags to prefer (e.g. more_protein, quick_prep) and ingredients/allergens to exclude.")]
    public Task<string> FindRecipes(
        [Description("Tags to prefer, e.g. [\"more_vegetables\",\"quick_prep\"]. Empty = all.")] string[]? preferTags = null,
        [Description("Ingredients or allergen groups that must NOT appear, e.g. [\"peanut\",\"mushrooms\"].")] string[]? excludeIngredients = null,
        [Description("Maximum prep minutes, e.g. 20 for a busy evening. 0 = no limit.")] int maxPrepMinutes = 0,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): read recipes.json, apply tag/exclusion/prep filters");

    [KernelFunction]
    [Description("Returns one recipe in full: ingredients, allergen 'contains' list, tags, prep minutes.")]
    public Task<string> GetRecipe(
        [Description("Recipe name or id, e.g. \"spinach dal rice bowl\" or \"rcp-001\".")] string nameOrId,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): lookup by id or fuzzy name");
}
