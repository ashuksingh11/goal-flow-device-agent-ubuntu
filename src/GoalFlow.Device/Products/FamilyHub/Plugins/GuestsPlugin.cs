using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (guest_dinner domain): hosted event, RSVPs, and guest
/// dietary constraints. Read-only grounding for dinner-party planning; hard
/// safety constraints still come from dispatch.constraints.hard.
/// </summary>
[Description("Guest dinner event, RSVPs, and dietary constraints.")]
public sealed class GuestsPlugin
{
    private readonly MockWorldStore _store;

    public GuestsPlugin(MockWorldStore store) => _store = store;

    [KernelFunction]
    [Description("Returns the upcoming hosted dinner event with resolved date, headcount, style, and timing.")]
    public async Task<string> GetEvent(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("guests", ct);
        var events = doc["events"]?.AsArray()
            .Select(n => n!.AsObject())
            .OrderBy(e => e["day_offset"]?.GetValue<int>() ?? int.MaxValue)
            .ToArray() ?? [];
        var upcoming = events.FirstOrDefault(e => (e["day_offset"]?.GetValue<int>() ?? int.MaxValue) >= 0)
            ?? events.FirstOrDefault()
            ?? throw new InvalidOperationException("No hosted guest event found.");

        var clone = upcoming.DeepClone().AsObject();
        clone["relative_to_clock"] = RelativeDay(upcoming["day_offset"]?.GetValue<int>() ?? 0);
        clone["attending_headcount"] = AttendingGuests(doc).Count();
        return Json(clone);
    }

    [KernelFunction]
    [Description("Returns the guest list with RSVP status and dietary constraints.")]
    public async Task<string> GetGuests(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("guests", ct);
        return Json(doc["guests"]);
    }

    [KernelFunction]
    [Description("Returns merged hard dietary constraints across attending guests, including allergies and no-beef/no-pork constraints.")]
    public async Task<string> GetDietaryConstraints(CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("guests", ct);
        var allergies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var dietary = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new JsonArray();

        foreach (var guest in AttendingGuests(doc))
        {
            var name = guest["name"]?.GetValue<string>() ?? "guest";
            var constraints = guest["dietary_constraints"]?.AsObject();
            AddAll(constraints?["allergies"]?.AsArray(), allergies);
            AddAll(constraints?["dietary"]?.AsArray(), dietary);
            AddAll(constraints?["avoid"]?.AsArray(), dietary, "no_");
            notes.Add(new JsonObject
            {
                ["guest"] = name,
                ["constraints"] = constraints?.DeepClone()
            });
        }

        return Json(new JsonObject
        {
            ["allergens"] = new JsonArray(allergies.Select(a => JsonValue.Create(a)).ToArray()),
            ["dietary"] = new JsonArray(dietary.Select(d => JsonValue.Create(d)).ToArray()),
            ["attending_guest_constraints"] = notes
        });
    }

    private static IEnumerable<JsonObject> AttendingGuests(JsonObject doc)
        => doc["guests"]?.AsArray()
            .Select(n => n!.AsObject())
            .Where(g => string.Equals(g["rsvp"]?.GetValue<string>(), "attending", StringComparison.OrdinalIgnoreCase))
            ?? [];

    private static void AddAll(JsonArray? values, ISet<string> target, string prefix = "")
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values.Select(v => v?.GetValue<string>()).Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            target.Add($"{prefix}{value}");
        }
    }

    private static string RelativeDay(int offset) => offset switch
    {
        0 => "today",
        1 => "tomorrow",
        _ when offset > 1 => $"in {offset} days",
        _ => $"{Math.Abs(offset)} days ago"
    };

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
