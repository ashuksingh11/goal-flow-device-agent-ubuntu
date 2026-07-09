using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harnesses.Adapters;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Sense-phase harness: the world-state assembler. Pulls from every product
/// API adapter and normalizes the results into ONE coherent
/// <see cref="WorldState"/> (scoped to the contract's time window, with
/// derived views like "expiring soon"). Build effort: FULL logic later.
/// </summary>
public interface IGrounding
{
    /// <summary>
    /// Assembles the world snapshot the planner and gates will consume.
    /// Timestamped from <see cref="IClock"/>, never wall-clock.
    /// </summary>
    Task<WorldState> AssembleAsync(Dispatch contract, CancellationToken cancellationToken = default);
}

/// <summary>Skeleton implementation — full logic in the implementation phase.</summary>
public sealed class Grounding : IGrounding
{
    private readonly IInventoryApi _inventory;
    private readonly ICalendarApi _calendar;
    private readonly IRecipeApi _recipes;
    private readonly IShoppingListApi _shoppingList;
    private readonly IReminderApi _reminders;
    private readonly IClock _clock;
    private readonly ITrace _trace;

    public Grounding(
        IInventoryApi inventory,
        ICalendarApi calendar,
        IRecipeApi recipes,
        IShoppingListApi shoppingList,
        IReminderApi reminders,
        IClock clock,
        ITrace trace)
    {
        _inventory = inventory;
        _calendar = calendar;
        _recipes = recipes;
        _shoppingList = shoppingList;
        _reminders = reminders;
        _clock = clock;
        _trace = trace;
    }

    public Task<WorldState> AssembleAsync(Dispatch contract, CancellationToken cancellationToken = default) =>
        // TODO:
        //  1. Fetch inventory, recipes, shopping list, reminders.
        //  2. Fetch calendar events overlapping contract.TimeWindow.
        //  3. Derive ExpiringSoon (expires_on inside the window), soonest first.
        //  4. Trace one event per adapter call; stamp AsOf from _clock.Now.
        throw new NotImplementedException("Design stub.");
}
