using System.ComponentModel;
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
    public Task<string> GetEvents(
        [Description("Window start, ISO date e.g. \"2026-07-13\".")] string startDate,
        [Description("Window end, ISO date (inclusive).")] string endDate,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): read calendar.json, resolve day_offset -> date, filter window");

    [KernelFunction]
    [Description("Lists evenings in the window where someone is busy around dinnertime — nights that need quick-prep meals.")]
    public Task<string> GetBusyEvenings(
        [Description("Window start, ISO date.")] string startDate,
        [Description("Window end, ISO date (inclusive).")] string endDate,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): events overlapping ~17:00-20:00 per day");

    [KernelFunction]
    [SideEffect(Contracts.ApprovalTiers.Light)]
    [Description("Adds an event to the family calendar (e.g. 'dinner prep' block or the guest dinner itself).")]
    public Task<string> AddEvent(
        [Description("Event title.")] string title,
        [Description("ISO date of the event.")] string date,
        [Description("Start time HH:mm.")] string start,
        [Description("End time HH:mm.")] string end,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): append event (stored as offset from today), persist");
}
