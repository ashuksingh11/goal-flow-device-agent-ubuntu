using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// Proactive suggestions from the fridge's own state (domain: the Family Hub).
///
/// <para>
/// Two deterministic scans of <c>inventory.json</c>:
/// <list type="bullet">
///   <item><b>Expiring Soon</b> — items whose <c>expires_in_days</c> resolves within the
///   horizon. The "use it up before it's waste" nudge.</item>
///   <item><b>Grocery Restock</b> — items at or below their <c>restock_threshold</c> (a
///   per-item minimum in the item's own unit, since quantities are mixed g/kg/pcs and a
///   flat number would compare grams to pieces).</item>
/// </list>
/// A scan with nothing to report emits no card — a suggestion the family can't act on is
/// noise, the same bar the observers hold for materiality.
/// </para>
/// </summary>
public sealed class InventorySuggester : ISuggester
{
    private const int ExpiryHorizonDays = 3;

    private readonly IProductApiAdapter _store;
    private readonly IClock _clock;

    public InventorySuggester(IProductApiAdapter store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SuggestionItem>> ScanAsync(CancellationToken ct = default)
    {
        var inventory = await _store.LoadResolvedAsync("inventory", ct);
        var items = (inventory["items"]?.AsArray() ?? []).Select(n => n!.AsObject()).ToArray();
        var suggestions = new List<SuggestionItem>();

        var expiring = items
            .Where(it => it["expires_in_days"] is not null
                         && it["expires_in_days"]!.GetValueKind() != System.Text.Json.JsonValueKind.Null
                         && it["expires_in_days"]!.GetValue<int>() <= ExpiryHorizonDays)
            .Select(it => it["name"]?.GetValue<string>() ?? "")
            .Where(n => n.Length > 0)
            .ToArray();
        if (expiring.Length > 0)
        {
            suggestions.Add(new SuggestionItem
            {
                Id = "sug-expiring",
                Kind = "expiring",
                Title = "Expiring Soon",
                Subtitle = $"{expiring.Length} item{(expiring.Length == 1 ? "" : "s")} within {ExpiryHorizonDays} days",
                Detail = string.Join(", ", expiring),
                GoalText = "Plan meals this week that use up the food expiring soon so nothing goes to waste"
            });
        }

        var low = items
            .Where(it => it["restock_threshold"] is not null
                         && (it["quantity"]?.GetValue<double>() ?? 0) <= it["restock_threshold"]!.GetValue<double>())
            .Select(it => it["name"]?.GetValue<string>() ?? "")
            .Where(n => n.Length > 0)
            .ToArray();
        if (low.Length > 0)
        {
            suggestions.Add(new SuggestionItem
            {
                Id = "sug-restock",
                Kind = "restock",
                Title = "Grocery Restock",
                Subtitle = $"{low.Length} item{(low.Length == 1 ? "" : "s")} running low",
                Detail = string.Join(", ", low),
                GoalText = "Order groceries to restock the staples that are running low"
            });
        }

        return suggestions;
    }
}
