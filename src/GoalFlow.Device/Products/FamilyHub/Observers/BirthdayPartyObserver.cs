using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// Watches a child's birthday party (domain <c>birthday_party</c>).
///
/// <para>
/// Distinct from <c>guest_dinner</c>: a dinner watches dietary RSVPs, a party watches
/// HEADCOUNT — how many children are coming drives the cake size, the number of party
/// bags, and whether the whole thing still fits the budget cap. The Budget and Notify
/// plugins (implemented in M7) are what make this concrete: estimate the cake and
/// decorations, send the invitations.
/// </para>
///
/// <para>
/// Changes come from <c>party.json</c>'s <c>pending_updates</c> — late RSVPs that move
/// the headcount, each with an <c>activation_day_offset</c> resolved against the clock.
/// The seeded one is three more children saying yes: material, because the plan was
/// sized for the old number.
/// </para>
/// </summary>
public sealed class BirthdayPartyObserver : IDomainObserver
{
    private readonly IProductApiAdapter _store;
    private readonly IClock _clock;

    public BirthdayPartyObserver(IProductApiAdapter store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public string Domain => "birthday_party";

    public string Hint => "planning a child's birthday party — guest list, invitations, cake and decorations within budget, schedule";

    public async Task<JsonObject> CaptureAsync(CancellationToken ct = default)
    {
        var party = await LoadPartyAsync(ct);
        ResolvePendingUpdates(party, _clock.Today);
        return new JsonObject
        {
            ["party"] = party,
            ["family"] = await _store.LoadResolvedAsync("family", ct),
            ["budget"] = await _store.LoadResolvedAsync("budget", ct),
            ["calendar"] = await _store.LoadResolvedAsync("calendar", ct)
        };
    }

    public IReadOnlyList<WorldChange> Observe(GoalRecord goal)
    {
        var today = _clock.Today.ToString("yyyy-MM-dd");
        var updates = goal.WorldSnapshot["party"]?["pending_updates"]?.AsArray() ?? [];

        return updates
            .Select(n => n?.AsObject())
            .OfType<JsonObject>()
            .Where(u => u["activation_date"]?.GetValue<string>() is { } act && string.CompareOrdinal(today, act) >= 0)
            .Select(u => BuildChange(u))
            .ToArray();
    }

    private static WorldChange BuildChange(JsonObject update)
    {
        var kind = update["kind"]?.GetValue<string>() ?? "party.update";
        var id = update["id"]?.GetValue<string>() ?? kind;
        var activation = update["activation_date"]?.GetValue<string>() ?? "";
        var delta = update["delta"]?.GetValue<int>() ?? 0;
        var description = update["description"]?.GetValue<string>() ?? "The guest count changed.";

        return new WorldChange
        {
            Key = $"party:{id}:{activation}",
            Kind = kind,
            Description = $"{description}. Activated on {activation}.",
            AffectedPlanItems = ["party-supplies"],
            RecommendedAction = $"Add supplies for {delta} more children and re-check the total against the budget.",
            EffectModule = "ShoppingList",
            EffectFunction = "Add",
            EffectArgs = new JsonObject
            {
                ["items"] = new JsonArray("extra party bags", "more juice boxes", "a larger cake"),
                ["reason"] = "birthday adaptation: headcount increased"
            },
            EffectAction = "add supplies for the extra guests",
            Material = IsMaterial(kind)
        };
    }

    private static bool IsMaterial(string kind) => kind switch
    {
        "party.headcount_added" => true,
        _ => false
    };

    private static void ResolvePendingUpdates(JsonObject party, DateOnly capturedOn)
    {
        foreach (var update in (party["pending_updates"]?.AsArray() ?? []).Select(n => n?.AsObject()).OfType<JsonObject>())
        {
            if (update["activation_day_offset"] is not null)
            {
                update["activation_date"] = capturedOn.AddDays(update["activation_day_offset"]!.GetValue<int>()).ToString("yyyy-MM-dd");
            }
        }
    }

    /// <summary>party.json is optional — absent means no RSVP changes, not a crash.</summary>
    private async Task<JsonObject> LoadPartyAsync(CancellationToken ct)
    {
        try
        {
            return await _store.LoadResolvedAsync("party", ct);
        }
        catch (FileNotFoundException)
        {
            return new JsonObject { ["pending_updates"] = new JsonArray() };
        }
        catch (DirectoryNotFoundException)
        {
            return new JsonObject { ["pending_updates"] = new JsonArray() };
        }
    }
}
