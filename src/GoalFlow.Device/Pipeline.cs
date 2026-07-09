using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harnesses;

namespace GoalFlow.Device;

/// <summary>
/// The device harness pipeline orchestrator:
/// <c>sense → decide → gate → act → sustain</c>, with Trace cross-cutting.
/// <para>
/// Transport-agnostic by design: Milestone 1 drives it from the command line
/// (Program.cs); later the WsClient shell feeds it deserialized dispatches.
/// The pipeline itself never knows where the contract came from.
/// </para>
/// </summary>
public sealed class Pipeline
{
    private readonly IPlanner _planner;
    private readonly IGrounding _grounding;
    private readonly ISafetyGate _safetyGate;
    private readonly IApprovalBroker _approvalBroker;
    private readonly IClock _clock;
    private readonly ITrace _trace;

    public Pipeline(
        IPlanner planner,
        IGrounding grounding,
        ISafetyGate safetyGate,
        IApprovalBroker approvalBroker,
        IClock clock,
        ITrace trace)
    {
        _planner = planner;
        _grounding = grounding;
        _safetyGate = safetyGate;
        _approvalBroker = approvalBroker;
        _clock = clock;
        _trace = trace;
    }

    /// <summary>
    /// Runs the planning half of the loop for one Task Contract and returns
    /// the <c>plan_ready</c> message to send (or print, in M1).
    /// </summary>
    public async Task<PlanReady> RunAsync(Dispatch contract, CancellationToken cancellationToken = default)
    {
        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = contract.GoalId,
            Phase = TracePhase.Orchestrate,
            Source = nameof(Pipeline),
            Kind = "received",
            Message = "dispatch accepted",
        });

        var world = await _grounding.AssembleAsync(contract, cancellationToken);

        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = contract.GoalId,
            Phase = TracePhase.Sense,
            Source = nameof(Pipeline),
            Kind = "world_snapshot",
            Message = $"world snapshot assembled with {world.Inventory.Count} inventory items, {world.Calendar.Count} events, {world.Recipes.Count} recipes",
        });

        var plan = await _planner.CreatePlanAsync(contract, world, cancellationToken);
        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = contract.GoalId,
            Phase = TracePhase.Decide,
            Source = plan.PlannerId,
            Kind = "plan_created",
            Message = $"candidate plan contains {plan.Plan.Count} meals and {plan.Proposals.Count} proposals",
        });

        var safety = _safetyGate.Check(plan, contract.Constraints.Hard, world);
        var proposals = safety.Gate == SafetyResult.GatePassed ? plan.Proposals : [];
        if (proposals.Count > 0)
        {
            _approvalBroker.Submit(contract.GoalId, contract.CorrelationId ?? contract.GoalId, proposals);
        }

        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = contract.GoalId,
            Phase = TracePhase.Orchestrate,
            Source = nameof(Pipeline),
            Kind = "completed",
            Message = "plan_ready assembled",
        });

        return new PlanReady
        {
            GoalId = contract.GoalId,
            CorrelationId = contract.CorrelationId ?? contract.GoalId,
            TaskStatus = TaskStatuses.AwaitingApproval,
            Payload = new PlanReadyPayload
            {
                Plan = plan.Plan,
                Proposals = proposals,
                Safety = safety,
            },
        };
    }

    /// <summary>
    /// Act phase: applies an incoming approval — transitions to "executing",
    /// executes approved proposals idempotently via the EffectExecutor, and
    /// returns a <c>status</c> message describing what was done.
    /// </summary>
    public Task<StatusMessage> OnApprovalAsync(Approval approval, CancellationToken cancellationToken = default)
    {
        // TODO:
        // 1. approvedList = _approvalBroker.ApplyDecisions(approval)   (dedupes replays)
        // 2. _taskManager.Transition(goalId, executing)
        // 3. foreach approved: _effectExecutor.ExecuteAsync(approved)  (idempotent)
        // 4. _taskManager.Transition(goalId, done) when nothing pending remains
        // 5. return status message summarizing outcomes.
        throw new NotImplementedException("Design stub — implementation phase.");
    }

    /// <summary>
    /// Sustain phase (M4): re-entry point invoked by the ChangeWatcher for a
    /// MATERIAL world change — re-grounds, re-plans the affected slice,
    /// re-gates, and returns an adaptation <c>proposal</c> message
    /// (task_status "adapting"). Never executes anything directly.
    /// </summary>
    public Task<Proposal> OnMaterialChangeAsync(
        string goalId,
        WorldChange change,
        CancellationToken cancellationToken = default)
    {
        // TODO(M4): re-run sense→decide→gate for the impacted portion; freeze
        // the resulting side-effect as a ProposalItem (trigger = change.Summary);
        // submit to _approvalBroker; return the proposal message.
        throw new NotImplementedException("Design stub — M4.");
    }
}
