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
    private readonly IEffectExecutor _effectExecutor;
    private readonly IClock _clock;
    private readonly ITrace _trace;

    public Pipeline(
        IPlanner planner,
        IGrounding grounding,
        ISafetyGate safetyGate,
        IApprovalBroker approvalBroker,
        IEffectExecutor effectExecutor,
        IClock clock,
        ITrace trace)
    {
        _planner = planner;
        _grounding = grounding;
        _safetyGate = safetyGate;
        _approvalBroker = approvalBroker;
        _effectExecutor = effectExecutor;
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
                Impact = ComputeImpact(plan, proposals, world),
            },
        };
    }

    /// <summary>
    /// Act phase: applies an incoming approval — transitions to "executing",
    /// executes approved proposals idempotently via the EffectExecutor, and
    /// returns a <c>status</c> message describing what was done.
    /// </summary>
    public async Task<IReadOnlyList<StatusMessage>> OnApprovalAsync(Approval approval, CancellationToken cancellationToken = default)
    {
        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = approval.GoalId,
            Phase = TracePhase.Orchestrate,
            Source = nameof(Pipeline),
            Kind = "approval_received",
            Message = $"{approval.Payload.Decisions.Count} approval decisions received",
        });

        var approved = _approvalBroker.ApplyDecisions(approval);
        var records = new List<EffectRecord>();
        foreach (var proposal in approved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(await _effectExecutor.ExecuteAsync(proposal, cancellationToken));
        }

        _approvalBroker.MarkExecuted(approved);

        var executed = records
            .Where(record => record.Outcome == "executed")
            .Select(record => new ExecutedEffect
            {
                ProposalId = record.ProposalId,
                Action = record.Action,
                Result = record.Result,
                Detail = record.Detail,
            })
            .ToArray();

        var executing = new StatusMessage
        {
            GoalId = approval.GoalId,
            CorrelationId = approval.CorrelationId,
            TaskStatus = TaskStatuses.Executing,
            Payload = new StatusPayload
            {
                Executed = executed,
                Note = approved.Count == 0
                    ? "no newly approved proposals to execute"
                    : $"executed {executed.Length} approved proposal(s)",
            },
        };

        var done = new StatusMessage
        {
            GoalId = approval.GoalId,
            CorrelationId = approval.CorrelationId,
            TaskStatus = TaskStatuses.Done,
            Payload = new StatusPayload
            {
                Executed = executed,
                Note = executed.Length == 0
                    ? "approval replay or no approved proposals; nothing new executed"
                    : $"{executed.Length} approved proposal(s) executed",
            },
        };

        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = approval.GoalId,
            Phase = TracePhase.Orchestrate,
            Source = nameof(Pipeline),
            Kind = "status_ready",
            Message = $"approval loop completed with {executed.Length} executed proposal(s)",
        });

        return [executing, done];
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

    private static ImpactMetrics ComputeImpact(
        CandidatePlan plan,
        IReadOnlyList<ProposalItem> proposals,
        WorldState world)
    {
        var recipesByName = world.Recipes.ToDictionary(recipe => Normalize(recipe.Name), StringComparer.Ordinal);
        var selectedRecipes = plan.Plan
            .Select(item => recipesByName.TryGetValue(Normalize(item.Dish), out var recipe) ? recipe : null)
            .Where(recipe => recipe is not null)
            .Cast<Recipe>()
            .ToArray();

        var expiringNames = world.ExpiringSoon
            .Select(item => Normalize(item.Name))
            .ToHashSet(StringComparer.Ordinal);
        var itemsUsedBeforeExpiry = selectedRecipes
            .SelectMany(recipe => recipe.Ingredients)
            .Select(Normalize)
            .Where(expiringNames.Contains)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var porkMeals = selectedRecipes.Count(recipe => RecipeTerms(recipe).Contains("pork"));
        var vegForwardDinners = selectedRecipes.Count(recipe =>
            recipe.Tags.Select(Normalize).Any(tag =>
                tag is "vegetarian" or "more vegetables" or "veg heavy" or "veg forward"));
        var groceryItems = proposals
            .Where(proposal => proposal.Action == "add_to_shopping_list")
            .SelectMany(proposal => proposal.Items ?? [])
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new ImpactMetrics
        {
            ItemsUsedBeforeExpiry = itemsUsedBeforeExpiry,
            PorkMeals = porkMeals,
            VegForwardDinners = vegForwardDinners,
            GroceryItems = groceryItems,
        };
    }

    private static HashSet<string> RecipeTerms(Recipe recipe)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in recipe.Ingredients.Concat(recipe.Contains).Concat(recipe.Tags).Append(recipe.Name))
        {
            var normalized = Normalize(value);
            terms.Add(normalized);
            foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                terms.Add(token);
            }
        }

        return terms;
    }

    private static string Normalize(string value) =>
        value.Trim().Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
}
