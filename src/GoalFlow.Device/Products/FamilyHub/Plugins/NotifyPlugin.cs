using System.ComponentModel;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device.Products.FamilyHub;

/// <summary>
/// CAPABILITY MODULE (shared): notifications and household announcements
/// (Hub screen banner / phones). SK plugin, name "Notify".
/// SIGNATURES ONLY in v2-M0. Announcements are checked by the SafetyFilter
/// against quiet_hours in constraints.hard.
/// </summary>
[Description("Sends notifications to family members or announces on the Hub.")]
[Unavailable("v2-M0 skeleton — every method throws NotImplementedException")]
public sealed class NotifyPlugin
{
    private readonly IClock _clock;

    public NotifyPlugin(IClock clock) => _clock = clock;

    [KernelFunction]
    [SideEffect(ApprovalTiers.Auto)]
    [Description("Sends a notification to one family member's phone.")]
    public Task<string> SendNotification(
        [Description("Member name, e.g. \"Priya\".")] string member,
        [Description("Notification text.")] string message,
        CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton");

    [KernelFunction]
    [SideEffect(ApprovalTiers.Light)]
    [Description("Announces a message on the Hub / all household devices at a time. Blocked during quiet hours.")]
    public Task<string> Announce(
        [Description("Announcement text.")] string message,
        [Description("Optional ISO date to announce on; null = now.")] string? date = null,
        [Description("Optional time HH:mm; null = now.")] string? time = null,
        CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 skeleton");
}
