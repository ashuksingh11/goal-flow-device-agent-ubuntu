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

    public async Task<WorldState> AssembleAsync(Dispatch contract, CancellationToken cancellationToken = default)
    {
        var start = DateOnly.Parse(contract.TimeWindow.Start);
        var end = DateOnly.Parse(contract.TimeWindow.End);

        var inventory = await _inventory.GetInventoryAsync(cancellationToken);
        TraceAdapter(contract.GoalId, "inventory", inventory.Count);

        var calendar = await _calendar.GetEventsAsync(start, end, cancellationToken);
        TraceAdapter(contract.GoalId, "calendar", calendar.Count);

        var recipes = await _recipes.GetRecipesAsync(cancellationToken);
        TraceAdapter(contract.GoalId, "recipes", recipes.Count);

        var shoppingList = await _shoppingList.GetListAsync(cancellationToken);
        TraceAdapter(contract.GoalId, "shopping_list", shoppingList.Count);

        var reminders = await _reminders.GetRemindersAsync(cancellationToken);
        TraceAdapter(contract.GoalId, "reminders", reminders.Count);

        var expiringSoon = inventory
            .Select(item => new { Item = item, HasDate = DateOnly.TryParse(item.ExpiresOn, out var date), Date = date })
            .Where(item => item.HasDate && item.Date >= _clock.Today && item.Date <= end)
            .OrderBy(item => item.Date)
            .Select(item => item.Item)
            .ToArray();

        return new WorldState
        {
            AsOf = _clock.Now,
            Inventory = inventory,
            Calendar = calendar,
            Recipes = recipes,
            ShoppingList = shoppingList,
            Reminders = reminders,
            ExpiringSoon = expiringSoon,
        };
    }

    private void TraceAdapter(string goalId, string adapter, int count) =>
        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = goalId,
            Phase = TracePhase.Sense,
            Source = nameof(Grounding),
            Kind = "adapter_read",
            Message = $"{adapter} returned {count} records",
        });
}
