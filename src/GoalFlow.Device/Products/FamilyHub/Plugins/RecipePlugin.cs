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

    /// <summary>
    /// THE WHOLE (SMALL) RECIPE BOX, plus an honest report of what the caller's tags matched.
    ///
    /// <para>
    /// v7.1: this used to return a bare array and nothing else, and that silence cost about
    /// four minutes per plan. The household prefers WHITE MEAT and workout-aligned protein,
    /// so the planner searched <c>white_meat</c> and <c>high_protein</c> — neither of which
    /// exists in this box's tag vocabulary (it says <c>more_protein</c>, and every recipe is
    /// vegetarian). The old filter answered by ORDERING on a preference count that was zero
    /// for every recipe: same five recipes, same order, HTTP 200. The model had no way to
    /// tell "your tags matched nothing" from "these are the best matches", so it assumed it
    /// had phrased the query wrong and retried — ten-plus times, with the tag list merely
    /// permuted, each retry a full LLM round-trip.
    /// </para>
    ///
    /// <para>
    /// So the result now NAMES the tags that matched nothing and lists the vocabulary that
    /// exists. A tool that cannot satisfy a query has to say so; one that quietly returns
    /// everything is lying by omission, and an agent's only recourse against a lie it cannot
    /// detect is to ask again.
    /// </para>
    /// </summary>
    // TERSE, not short of facts, and the FIRST CLAUSE now leads. Three instructions here
    // are load-bearing and were each paid for once: call once (the v7.1 retry loop), never
    // follow with GetRecipe (the v7.2 seven-round-trip loop), preferTags only re-orders
    // (why a "no match" reply is not a reason to rephrase). Trim those and the loops come
    // back — gate 28. But they are addressed to a MODEL, and SafetyFilter.Describe prints
    // the opening clause of this string into the user's transcript as the step headline,
    // so the sentence a person reads has to come first: "Returns the whole recipe box".
    [KernelFunction]
    [Description("Returns the whole recipe box — every recipe complete, with ingredients, tags, " +
                 "allergen 'contains' and prep_minutes. Call once; do not repeat it and do not follow " +
                 "it with GetRecipe. preferTags only re-orders; the reply says which tags exist.")]
    public async Task<string> FindRecipes(
        [Description("Tags to prefer. The reply lists the real vocabulary under 'available_tags' and names any that matched nothing.")] string[]? preferTags = null,
        [Description("Ingredients or allergen groups that must NOT appear, e.g. [\"peanut\",\"mushrooms\"].")] string[]? excludeIngredients = null,
        [Description("Maximum prep minutes, e.g. 20 for a busy evening. 0 = no limit.")] int maxPrepMinutes = 0,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("recipes", ct);
        var all = doc["recipes"]?.AsArray().Select(n => n!.AsObject()).ToArray() ?? [];

        var prefer = new HashSet<string>(preferTags ?? [], StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(excludeIngredients ?? [], StringComparer.OrdinalIgnoreCase);
        var vocabulary = new HashSet<string>(
            all.SelectMany(r => r["tags"]!.AsArray().Select(t => t!.GetValue<string>())),
            StringComparer.OrdinalIgnoreCase);

        var recipes = all
            .Where(r => maxPrepMinutes <= 0 || r["prep_minutes"]!.GetValue<int>() <= maxPrepMinutes)
            .Where(r => !ContainsAny(r["ingredients"]!.AsArray(), exclude) && !ContainsAny(r["contains"]!.AsArray(), exclude))
            .OrderByDescending(r => prefer.Count == 0 ? 0 : r["tags"]!.AsArray().Count(t => prefer.Contains(t!.GetValue<string>())))
            .Select(r => r.DeepClone())
            .ToArray();

        var unmatched = prefer.Where(t => !vocabulary.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var matched = prefer.Where(vocabulary.Contains).OrderBy(t => t, StringComparer.Ordinal).ToArray();

        var result = new JsonObject
        {
            ["count"] = recipes.Length,
            ["matched_tags"] = new JsonArray(matched.Select(t => (JsonNode)t!).ToArray()),
            ["unmatched_tags"] = new JsonArray(unmatched.Select(t => (JsonNode)t!).ToArray()),
            ["available_tags"] = new JsonArray(vocabulary.OrderBy(t => t, StringComparer.Ordinal).Select(t => (JsonNode)t!).ToArray()),
            ["recipes"] = new JsonArray(recipes),
        };
        // The instruction is as important as the data: without it a model that reads
        // "unmatched" still has the option of trying a synonym, which is the loop again.
        if (unmatched.Length > 0)
        {
            result["note"] =
                $"No recipe carries {string.Join(", ", unmatched)}. This box has no other recipes — "
                + "the list above is ALL of them. Do NOT search again with different words; plan from these, "
                + "or say plainly that the box cannot satisfy that preference.";
        }
        return Json(result);
    }

    /// <summary>
    /// One recipe, for the caller who has not fetched the box.
    ///
    /// <para>
    /// v7.2: the description now says outright that this is redundant after FindRecipes,
    /// because a planner was calling it once per recipe — seven extra round-trips fetching
    /// fields it was already holding. That is not a foolish reading of the old wording
    /// ("returns one recipe in full") — it just never said the other tool had already done
    /// this. A tool description is the only documentation the model gets, and one that
    /// omits its relationship to the tool beside it will be used as if it had none.
    /// </para>
    /// </summary>
    [KernelFunction]
    [Description("Returns ONE recipe in full: ingredients, allergen 'contains' list, tags, prep minutes. " +
                 "Use this only if you have NOT called FindRecipes — that returns every recipe with these " +
                 "same fields already, so calling this afterwards just re-fetches what you have.")]
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
