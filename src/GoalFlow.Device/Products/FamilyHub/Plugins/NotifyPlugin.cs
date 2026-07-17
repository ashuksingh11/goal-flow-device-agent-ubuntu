using System.ComponentModel;
using System.Text.Json.Nodes;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (shared): notifications and household announcements
/// (Hub screen banner / phones). SK plugin, name "Notify". Sends are recorded to
/// data/notifications.json through <see cref="IProductApiAdapter"/>, so a sent
/// message is a real inspectable side effect rather than a no-op.
///
/// Announcements carry a time and are checked by the SafetyFilter against
/// quiet_hours in constraints.hard (the time_window_block rule) BEFORE they reach
/// here — so a quiet-hours violation never gets logged.
/// </summary>
[Description("Sends notifications to family members or announces on the Hub.")]
public sealed class NotifyPlugin
{
    private readonly IProductApiAdapter _store;
    private readonly IClock _clock;

    public NotifyPlugin(IProductApiAdapter store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Sends a notification to one family member's phone (e.g. a party invitation, a reminder).")]
    public async Task<string> SendNotification(
        [Description("Member or guest name, e.g. \"Priya\".")] string member,
        [Description("Notification text.")] string message,
        CancellationToken ct = default)
    {
        var id = await RecordAsync("notification", new JsonObject
        {
            ["to"] = member,
            ["message"] = message
        }, date: null, ct);

        return Json(new JsonObject
        {
            ["status"] = "sent",
            ["id"] = id,
            ["to"] = member,
            ["message"] = message
        });
    }

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Announces a message on the Hub / all household devices at a time. Blocked during quiet hours.")]
    public async Task<string> Announce(
        [Description("Announcement text.")] string message,
        [Description("Optional ISO date to announce on; null = now.")] string? date = null,
        [Description("Optional time HH:mm; null = now.")] string? time = null,
        CancellationToken ct = default)
    {
        var id = await RecordAsync("announcement", new JsonObject
        {
            ["message"] = message,
            ["time"] = time
        }, date, ct);

        return Json(new JsonObject
        {
            ["status"] = "announced",
            ["id"] = id,
            ["message"] = message,
            ["at_date"] = date,
            ["at_time"] = time
        });
    }

    /// <summary>
    /// Appends one entry to the sent log and returns its id. A supplied date is
    /// stored as a day_offset from today, never absolute — the same generic-clock
    /// rule every mock document follows, so a reset/replay stays clock-correct.
    /// </summary>
    private async Task<string> RecordAsync(string kind, JsonObject detail, string? date, CancellationToken ct)
    {
        var doc = await _store.LoadResolvedAsync("notifications", ct);
        var sent = doc["sent"]?.AsArray();
        if (sent is null)
        {
            sent = [];
            doc["sent"] = sent;
        }

        var id = $"ntf-{sent.Count + 1:000}";
        var entry = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["detail"] = detail,
            ["sent_day_offset"] = 0
        };
        if (date is not null)
        {
            entry["scheduled_day_offset"] = _store.OffsetFromToday(date);
        }
        sent.Add(entry);
        await _store.SaveAsync("notifications", doc, ct);
        return id;
    }

    private static string Json(JsonNode? node)
        => (node ?? new JsonObject()).ToJsonString(ContractJson.Options);
}
