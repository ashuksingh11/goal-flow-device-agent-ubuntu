using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Steering;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Capabilities;

/// <summary>
/// CAPABILITY MODULE (shared): the family's shared calendar.
/// SK plugin, name "Calendar". Backed by data/calendar.json (events stored
/// with day_offset relative to today, resolved via the clock). Read-only in
/// the meal domain; guest_dinner also adds events (later milestone).
/// </summary>
[Description("The shared family calendar — who is busy when.")]
public sealed class CalendarPlugin
{
    private readonly MockWorldStore _store;

    public CalendarPlugin(MockWorldStore store) => _store = store;

    [KernelFunction]
    [Description("Lists calendar events between two ISO dates (inclusive), with attendee and start/end times.")]
    public async Task<string> GetEvents(
        [Description("Window start, ISO date e.g. \"2026-07-13\".")] string startDate,
        [Description("Window end, ISO date (inclusive).")] string endDate,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("calendar", ct);
        var start = DateOnly.Parse(startDate);
        var end = DateOnly.Parse(endDate);
        var events = doc["events"]?.AsArray()
            .Where(n =>
            {
                var date = DateOnly.Parse(n!["date"]!.GetValue<string>());
                return date >= start && date <= end;
            })
            .Select(n => n!.DeepClone())
            .ToArray() ?? [];
        return Json(new JsonArray(events));
    }

    [KernelFunction]
    [Description("Lists evenings in the window where someone is busy around dinnertime — nights that need quick-prep meals.")]
    public async Task<string> GetBusyEvenings(
        [Description("Window start, ISO date.")] string startDate,
        [Description("Window end, ISO date (inclusive).")] string endDate,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("calendar", ct);
        var start = DateOnly.Parse(startDate);
        var end = DateOnly.Parse(endDate);
        var dinnerStart = TimeOnly.Parse("17:00");
        var dinnerEnd = TimeOnly.Parse("20:00");
        var busy = doc["events"]?.AsArray()
            .Where(n =>
            {
                var date = DateOnly.Parse(n!["date"]!.GetValue<string>());
                if (date < start || date > end) return false;
                var s = TimeOnly.Parse(n["start"]!.GetValue<string>());
                var e = TimeOnly.Parse(n["end"]!.GetValue<string>());
                return s < dinnerEnd && e > dinnerStart;
            })
            .Select(n => new JsonObject
            {
                ["date"] = n!["date"]!.GetValue<string>(),
                ["title"] = n["title"]!.GetValue<string>(),
                ["attendee"] = n["attendee"]?.GetValue<string>(),
                ["start"] = n["start"]!.GetValue<string>(),
                ["end"] = n["end"]!.GetValue<string>(),
                ["suggestion"] = "quick_prep"
            })
            .Cast<JsonNode?>()
            .ToArray() ?? [];
        return Json(new JsonArray(busy));
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Adds an event to the family calendar (e.g. 'dinner prep' block or the guest dinner itself).")]
    public async Task<string> AddEvent(
        [Description("Event title.")] string title,
        [Description("ISO date of the event.")] string date,
        [Description("Start time HH:mm.")] string start,
        [Description("End time HH:mm.")] string end,
        CancellationToken ct = default)
    {
        var doc = await _store.LoadResolvedAsync("calendar", ct);
        var events = doc["events"]!.AsArray();
        var id = $"cal-{events.Count + 1:000}";
        events.Add(new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["day_offset"] = _store.OffsetFromToday(date),
            ["start"] = start,
            ["end"] = end,
            ["attendee"] = "family"
        });
        await _store.SaveAsync("calendar", doc, ct);
        return Json(new JsonObject { ["status"] = "added", ["id"] = id });
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
