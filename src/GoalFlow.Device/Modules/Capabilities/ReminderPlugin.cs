using System.ComponentModel;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Steering;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Modules.Capabilities;

/// <summary>
/// CAPABILITY MODULE (shared): reminders/notes on the Hub screen.
/// SK plugin, name "Reminders". Backed by data/reminders.json (due dates
/// stored as day offsets). Creating a reminder is the canonical AUTO-tier
/// side-effect: cheap and reversible, so the agent may just do it.
/// </summary>
[Description("Family reminders and notes shown on the Hub.")]
public sealed class ReminderPlugin
{
    private readonly MockWorldStore _store;

    public ReminderPlugin(MockWorldStore store) => _store = store;

    [KernelFunction]
    [Description("Lists all active reminders with their due date/time.")]
    public Task<string> List(CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): read reminders.json, resolve due offsets");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Creates a reminder (e.g. 'defrost the paneer tonight'). Cheap and reversible — auto tier.")]
    public Task<string> Create(
        [Description("Reminder text.")] string title,
        [Description("Due ISO date, e.g. \"2026-07-14\".")] string date,
        [Description("Optional due time HH:mm.")] string? time = null,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): append (stored as offset from today), persist");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Deletes a reminder by id.")]
    public Task<string> Delete(
        [Description("Reminder id, e.g. \"rem-001\".")] string id,
        CancellationToken ct = default)
        => throw new NotImplementedException("TODO(M1): remove by id, persist");
}
