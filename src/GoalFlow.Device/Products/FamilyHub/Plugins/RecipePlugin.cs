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
    [Description("Returns the recipe box — every recipe the house can cook, complete, with ingredients, " +
                 "tags, allergen 'contains' and prep_minutes. Call once; do not repeat it and do not follow " +
                 "it with GetRecipe. preferTags only re-orders; the reply says which tags exist, and reports " +
                 "any recipe held back because it needs a fresh ingredient the house does not have.")]
    public async Task<string> FindRecipes(
        [Description("Tags to prefer. The reply lists the real vocabulary under 'available_tags' and names any that matched nothing.")] string[]? preferTags = null,
        [Description("Ingredients or allergen groups that must NOT appear, e.g. [\"peanut\",\"mushrooms\"].")] string[]? excludeIngredients = null,
        [Description("Maximum prep minutes, e.g. 20 for a busy evening. 0 = no limit.")] int maxPrepMinutes = 0,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("recipes", ct);
        var box = doc["recipes"]?.AsArray().Select(n => n!.AsObject()).ToArray() ?? [];

        // v12.2 — WITHHOLD A RECIPE WHOSE FRESH INGREDIENT IS NOT IN THE HOUSE.
        //
        // `requires_fresh` names an ingredient you cannot plan a week around. You cook it
        // on the day it arrives. Only one recipe carries the field today (rcp-007, fish).
        //
        // This is NOT a general stock filter, and it must not become one. The fridge also
        // holds no beef, no lamb and no turkey; a general filter would cut this box from
        // ten recipes to about five and would be wrong, because the planner is supposed to
        // put the missing items on the shopping list.
        //
        // It is not silent either — see the note below. A tool that quietly returns less
        // than everything is the v7.1 lie that cost four minutes a plan.
        var stock = await FreshStockAsync(ct);
        var withheld = box.Where(r => MissingFresh(r, stock).Length > 0).ToArray();
        var all = box.Where(r => MissingFresh(r, stock).Length == 0).ToArray();

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
        // Say what was held back, and say the rule. Naming the RECIPE would invite the
        // model to buy the ingredient and plan it anyway, which is the bug this fix
        // exists to stop — so the count and the reason go out, and the body does not.
        if (withheld.Length > 0)
        {
            var needed = withheld
                .SelectMany(r => MissingFresh(r, stock))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToArray();
            result["withheld_count"] = withheld.Length;
            result["withheld_reason"] =
                $"{withheld.Length} recipe(s) are not listed. Each needs {string.Join(", ", needed)} "
                + "FRESH IN THE HOUSE on the day it is cooked, and the fridge has none. "
                + "Do not plan around it and do not add it to the shopping list — it is bought "
                + "fresh, not stocked. Plan from the recipes above. If it arrives later, it will "
                + "appear here and can be cooked that day.";
        }
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

    /// <summary>
    /// Names of everything in the fridge with a quantity above zero (v12.2).
    /// </summary>
    private async Task<string[]> FreshStockAsync(CancellationToken ct)
    {
        var doc = await _store.LoadResolvedAsync("inventory", ct);
        return doc["items"]?.AsArray()
            .Select(n => n!.AsObject())
            .Where(i => (i["quantity"]?.GetValue<double>() ?? 0) > 0)
            .Select(i => i["name"]?.GetValue<string>() ?? string.Empty)
            .Where(n => n.Length > 0)
            .ToArray() ?? [];
    }

    /// <summary>
    /// The recipe's <c>requires_fresh</c> terms that the house does not hold (v12.2).
    /// Empty for every recipe without the field, which is nine of the ten.
    /// </summary>
    /// <remarks>
    /// The match is a SUBSTRING both ways on purpose. The recipe says "fish"; the
    /// delivery event writes an item named "fish", but a later event could write "white
    /// fish fillet" or "sea fish", and an exact-name test would keep the recipe hidden
    /// with the ingredient sitting in the fridge. That failure is invisible: the plan is
    /// simply worse, and nothing reports why.
    /// </remarks>
    internal static string[] MissingFresh(JsonObject recipe, string[] stock)
    {
        var required = recipe["requires_fresh"]?.AsArray();
        if (required is null || required.Count == 0)
        {
            return [];
        }
        return required
            .Select(n => n!.GetValue<string>())
            .Where(term => !stock.Any(item =>
                item.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                term.Contains(item, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
