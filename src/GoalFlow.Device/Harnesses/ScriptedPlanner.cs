using GoalFlow.Device.Contracts;
using System.Text.Json;

namespace GoalFlow.Device.Harnesses;

/// <summary>
/// Scripted/mock fallback planner: returns a canned plan loaded from a
/// fixture file (see data/golden-plan_ready.json). Used for Milestone 1
/// smoke runs and as the last-resort fallback when both RulesPlanner input
/// data and the LLM are unavailable. Deterministic by construction.
/// </summary>
public sealed class ScriptedPlanner : IPlanner
{
    private readonly string _fixturePath;

    /// <param name="fixturePath">Path to a plan_ready-shaped JSON fixture whose payload is replayed.</param>
    public ScriptedPlanner(string fixturePath) => _fixturePath = fixturePath;

    public Task<CandidatePlan> CreatePlanAsync(
        Dispatch contract,
        WorldState world,
        CancellationToken cancellationToken = default)
    {
        var json = File.ReadAllText(_fixturePath);
        var fixture = JsonSerializer.Deserialize<PlanReady>(json, ContractJson.Options)
            ?? throw new InvalidOperationException($"Unable to deserialize scripted plan fixture '{_fixturePath}'.");

        return Task.FromResult(new CandidatePlan
        {
            Plan = fixture.Payload.Plan,
            Proposals = fixture.Payload.Proposals,
            PlannerId = "scripted",
        });
    }
}
