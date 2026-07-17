using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (meal domain): the recipe box (Samsung Food stand-in).
/// SK plugin, name "Recipes". Backed by data/recipes.json (no dates).
/// Read-only. The SafetyFilter screens recipe choices indirectly: side-effects
/// derived from a recipe (shopping adds, reminders) carry its ingredients.
/// </summary>
[Description("Recipe search and details: ingredients, allergen tags, prep time.")]
public sealed class RecipePlugin
{
    private readonly IProductApiAdapter _store;

    public RecipePlugin(IProductApiAdapter store) => _store = store;

    [KernelFunction]
    [Description("Finds recipes, optionally filtered by tags to prefer (e.g. more_protein, quick_prep) and ingredients/allergens to exclude.")]
    public async Task<string> FindRecipes(
        [Description("Tags to prefer, e.g. [\"more_vegetables\",\"quick_prep\"]. Empty = all.")] string[]? preferTags = null,
        [Description("Ingredients or allergen groups that must NOT appear, e.g. [\"peanut\",\"mushrooms\"].")] string[]? excludeIngredients = null,
        [Description("Maximum prep minutes, e.g. 20 for a busy evening. 0 = no limit.")] int maxPrepMinutes = 0,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("recipes", ct);
        var prefer = new HashSet<string>(preferTags ?? [], StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(excludeIngredients ?? [], StringComparer.OrdinalIgnoreCase);
        var recipes = doc["recipes"]?.AsArray()
            .Select(n => n!.AsObject())
            .Where(r => maxPrepMinutes <= 0 || r["prep_minutes"]!.GetValue<int>() <= maxPrepMinutes)
            .Where(r => !ContainsAny(r["ingredients"]!.AsArray(), exclude) && !ContainsAny(r["contains"]!.AsArray(), exclude))
            .OrderByDescending(r => prefer.Count == 0 ? 0 : r["tags"]!.AsArray().Count(t => prefer.Contains(t!.GetValue<string>())))
            .Select(r => r.DeepClone())
            .ToArray() ?? [];
        return Json(new JsonArray(recipes));
    }

    [KernelFunction]
    [Description("Returns one recipe in full: ingredients, allergen 'contains' list, tags, prep minutes.")]
    public async Task<string> GetRecipe(
        [Description("Recipe name or id, e.g. \"spinach dal rice bowl\" or \"rcp-001\".")] string nameOrId,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("recipes", ct);
        var match = doc["recipes"]?.AsArray()
            .Select(n => n!.AsObject())
            .FirstOrDefault(r =>
                string.Equals(r["id"]?.GetValue<string>(), nameOrId, StringComparison.OrdinalIgnoreCase) ||
                r["name"]?.GetValue<string>().Contains(nameOrId, StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new InvalidOperationException($"Recipe '{nameOrId}' was not found.");
        return Json(match);
    }

    private static bool ContainsAny(JsonArray values, HashSet<string> exclude)
        => exclude.Count > 0 && values.Any(v => exclude.Contains(v!.GetValue<string>()));

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
