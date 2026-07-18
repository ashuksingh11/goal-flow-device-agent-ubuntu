using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Agent;

/// <summary>
/// The frames a WORLD-level control tick produces (see
/// <see cref="GoalAgent.HandleWorldControlAsync"/>): one <see cref="Status"/> per active
/// goal, an adaptation <see cref="Proposal"/> for each goal that newly caught a material
/// change, and one <see cref="DayAdvanced"/> summarising the day's world events. Program
/// sends them all in order.
/// </summary>
public sealed record ControlResult(
    IReadOnlyList<Status> Statuses,
    IReadOnlyList<Proposal> Proposals,
    DayAdvanced? DayAdvanced);
