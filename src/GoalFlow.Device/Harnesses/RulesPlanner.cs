using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Default planner: deterministic, demo-safe, no network. Build effort: FULL
/// logic later.
/// <para>Intended selection heuristics (implementation phase):</para>
/// <list type="number">
///   <item>Prefer recipes consuming <see cref="WorldState.ExpiringSoon"/> items (reduce_waste).</item>
///   <item>Honor soft preferences (prefer tags) and avoid soft dislikes.</item>
///   <item>On days with evening calendar events, pick low <c>prep_minutes</c> recipes.</item>
///   <item>Diff recipe ingredients vs inventory → missing items become ONE
///         add_to_shopping_list proposal.</item>
/// </list>
/// Note: it may use soft constraints as scoring hints but performs NO
/// hard-constraint check — that is the safety gate's job.
/// </summary>
public sealed class RulesPlanner : IPlanner
{
    private readonly ITrace _trace;
    private readonly IClock _clock;

    public RulesPlanner(ITrace trace, IClock clock)
    {
        _trace = trace;
        _clock = clock;
    }

    public Task<CandidatePlan> CreatePlanAsync(
        Dispatch contract,
        WorldState world,
        CancellationToken cancellationToken = default)
    {
        var available = world.Recipes
            .Where(recipe => RespectsHardHints(recipe, contract.Constraints.Hard))
            .ToList();
        if (available.Count == 0)
        {
            throw new InvalidOperationException("No recipes remain after hard-constraint filtering.");
        }

        var selected = new List<(PlanItem Item, Recipe Recipe)>();
        var usedRecipeIds = new HashSet<string>(StringComparer.Ordinal);
        var datesByDay = DatesForScope(contract);

        foreach (var day in contract.Scope.Days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var busyEvent = datesByDay.TryGetValue(day, out var date)
                ? EveningEvent(world.Calendar, date)
                : null;
            var candidate = available
                .OrderByDescending(recipe => Score(recipe, contract, world, busyEvent, usedRecipeIds.Contains(recipe.Id)))
                .ThenBy(recipe => busyEvent is null ? 0 : recipe.PrepMinutes)
                .ThenBy(recipe => recipe.Id, StringComparer.Ordinal)
                .ThenBy(recipe => recipe.Name, StringComparer.Ordinal)
                .First();

            usedRecipeIds.Add(candidate.Id);
            var why = Why(candidate, contract, world, busyEvent).Distinct(StringComparer.Ordinal).ToArray();
            selected.Add((new PlanItem { Day = day, Dish = candidate.Name, Why = why }, candidate));

            _trace.Record(new TraceEvent
            {
                At = _clock.Now,
                GoalId = contract.GoalId,
                Phase = TracePhase.Decide,
                Source = nameof(RulesPlanner),
                Kind = "recipe_selected",
                Message = $"{day}: {candidate.Name}",
            });
        }

        var inventoryNames = world.Inventory.Select(item => Normalize(item.Name)).ToHashSet(StringComparer.Ordinal);
        var shoppingNames = world.ShoppingList.Select(item => Normalize(item.Item)).ToHashSet(StringComparer.Ordinal);
        var missing = selected
            .SelectMany(item => item.Recipe.Ingredients)
            .Where(ingredient => !inventoryNames.Contains(Normalize(ingredient)))
            .Where(ingredient => !shoppingNames.Contains(Normalize(ingredient)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var proposals = missing.Length == 0
            ? []
            : new[]
            {
                new ProposalItem
                {
                    ProposalId = "p1",
                    Action = "add_to_shopping_list",
                    Items = missing,
                    Reason = $"needed for {string.Join(", ", selected.Select(item => item.Item.Day).Distinct(StringComparer.Ordinal))} dishes",
                    RequiresApproval = true,
                },
            };

        return Task.FromResult(new CandidatePlan
        {
            Plan = selected.Select(item => item.Item).ToArray(),
            Proposals = proposals,
            PlannerId = "rules",
        });
    }

    private static int Score(
        Recipe recipe,
        Dispatch contract,
        WorldState world,
        CalendarEvent? busyEvent,
        bool alreadyUsed)
    {
        var score = alreadyUsed ? -1000 : 0;
        var recipeTerms = RecipeTerms(recipe);

        foreach (var preference in contract.Constraints.Soft?.Prefer ?? [])
        {
            if (recipe.Tags.Any(tag => Normalize(tag) == Normalize(preference)))
            {
                score += 8;
            }
        }

        foreach (var dislike in contract.Constraints.Soft?.Dislikes ?? [])
        {
            if (recipeTerms.Contains(Normalize(dislike)))
            {
                score -= 20;
            }
        }

        foreach (var expiring in world.ExpiringSoon)
        {
            if (recipe.Ingredients.Any(ingredient => Normalize(ingredient) == Normalize(expiring.Name)))
            {
                score += 12;
                if (expiring.ExpiresOn == world.ExpiringSoon.FirstOrDefault()?.ExpiresOn)
                {
                    score += 10;
                }
            }
        }

        if (contract.Optimization.Any(opt => Normalize(opt) == "reduce waste") &&
            recipe.Ingredients.Any(ingredient => world.Inventory.Any(inv => Normalize(inv.Name) == Normalize(ingredient))))
        {
            score += 5;
        }

        if (busyEvent is not null)
        {
            score += recipe.PrepMinutes <= 20 ? 100 : -10;
            if (recipe.Tags.Any(tag => Normalize(tag) is "light" or "quick prep" or "quick"))
            {
                score += 25;
            }
        }

        return score;
    }

    private static IEnumerable<string> Why(Recipe recipe, Dispatch contract, WorldState world, CalendarEvent? busyEvent)
    {
        foreach (var preference in contract.Constraints.Soft?.Prefer ?? [])
        {
            if (recipe.Tags.Any(tag => Normalize(tag) == Normalize(preference)))
            {
                yield return preference;
            }
        }

        if (recipe.Ingredients.Any(ingredient => world.Inventory.Any(inv => Normalize(inv.Name) == Normalize(ingredient))))
        {
            yield return "uses_inventory";
        }

        foreach (var expiring in world.ExpiringSoon)
        {
            if (recipe.Ingredients.Any(ingredient => Normalize(ingredient) == Normalize(expiring.Name)))
            {
                yield return $"{Normalize(expiring.Name).Replace(' ', '_')}_expires_{expiring.ExpiresOn}";
            }
        }

        if (busyEvent is not null)
        {
            if (recipe.PrepMinutes <= 20 || recipe.Tags.Any(tag => Normalize(tag) is "quick" or "quick prep"))
            {
                yield return "quick_prep";
            }

            if (recipe.Tags.Any(tag => Normalize(tag) == "light"))
            {
                yield return "light";
            }

            yield return $"calendar_{Normalize(busyEvent.Title).Replace(' ', '_')}_{busyEvent.Start}";
        }

        foreach (var tag in recipe.Tags.Where(tag => Normalize(tag) is "light" or "kid friendly"))
        {
            yield return Normalize(tag).Replace(' ', '_');
        }
    }

    private static bool RespectsHardHints(Recipe recipe, HardConstraints hard)
    {
        var blocked = hard.Allergens
            .Concat(hard.Dietary.SelectMany(ExpandRule))
            .Concat(hard.Medical)
            .Select(Normalize)
            .Where(term => term.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return !RecipeTerms(recipe).Any(blocked.Contains);
    }

    private static HashSet<string> RecipeTerms(Recipe recipe) =>
        recipe.Ingredients
            .Concat(recipe.Contains)
            .Concat(recipe.Tags)
            .SelectMany(value => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> ExpandRule(string rule)
    {
        yield return rule;
        if (rule.StartsWith("no_", StringComparison.OrdinalIgnoreCase))
        {
            yield return rule["no_".Length..];
        }

        if (rule.StartsWith("without_", StringComparison.OrdinalIgnoreCase))
        {
            yield return rule["without_".Length..];
        }
    }

    private static Dictionary<string, DateOnly> DatesForScope(Dispatch contract)
    {
        var start = DateOnly.Parse(contract.TimeWindow.Start);
        return contract.Scope.Days
            .Select((day, index) => new { Day = day, Date = start.AddDays(index) })
            .ToDictionary(item => item.Day, item => item.Date, StringComparer.OrdinalIgnoreCase);
    }

    private static CalendarEvent? EveningEvent(IReadOnlyList<CalendarEvent> events, DateOnly date) =>
        events.FirstOrDefault(evt =>
            DateOnly.TryParse(evt.Date, out var eventDate) &&
            eventDate == date &&
            TimeOnly.TryParse(evt.Start, out var start) &&
            start >= new TimeOnly(18, 0) &&
            start <= new TimeOnly(20, 30));

    private static string Normalize(string value) =>
        value.Trim().Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
}
