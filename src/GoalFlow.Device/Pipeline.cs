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
    private readonly IScheduler _scheduler;
    private readonly IChangeWatcher _changeWatcher;
    private readonly IClock _clock;
    private readonly ITrace _trace;
    private readonly DateTimeOffset _clockAnchor;
    private readonly Action? _resetData;
    private Dispatch? _activeDispatch;
    private CandidatePlan? _activePlan;
    private int _adaptationSequence;

    public Pipeline(
        IPlanner planner,
        IGrounding grounding,
        ISafetyGate safetyGate,
        IApprovalBroker approvalBroker,
        IEffectExecutor effectExecutor,
        IScheduler scheduler,
        IChangeWatcher changeWatcher,
        IClock clock,
        ITrace trace,
        DateTimeOffset clockAnchor,
        Action? resetData = null)
    {
        _planner = planner;
        _grounding = grounding;
        _safetyGate = safetyGate;
        _approvalBroker = approvalBroker;
        _effectExecutor = effectExecutor;
        _scheduler = scheduler;
        _changeWatcher = changeWatcher;
        _clock = clock;
        _trace = trace;
        _clockAnchor = clockAnchor;
        _resetData = resetData;
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
        _activeDispatch = contract;
        _activePlan = plan;
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
        var proposalId = $"a{++_adaptationSequence}";
        var correlationId = $"{change.Source}-{change.EventDate ?? _clock.Today.ToString("yyyy-MM-dd")}-{proposalId}";
        var item = new ProposalItem
        {
            ProposalId = proposalId,
            Action = "add_prep_task",
            Detail = $"prep {change.PlannedDay ?? "affected day"}'s dish before the evening crunch",
            Trigger = change.Summary,
            RequiresApproval = true,
        };

        _approvalBroker.Submit(goalId, correlationId, [item]);
        var proposal = new Proposal
        {
            GoalId = goalId,
            CorrelationId = correlationId,
            TaskStatus = TaskStatuses.Adapting,
            Payload = item,
        };

        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = goalId,
            Phase = TracePhase.Sustain,
            Source = nameof(Pipeline),
            Kind = "adaptation_proposal",
            Message = $"{item.ProposalId}: {item.Trigger}",
        });

        return Task.FromResult(proposal);
    }

    public Task<IReadOnlyList<object>> OnControlAsync(Control control, CancellationToken cancellationToken = default)
    {
        return control.Command switch
        {
            ControlCommands.AdvanceDay => AdvanceDayControlAsync(control, cancellationToken),
            ControlCommands.Reset => ResetControlAsync(control),
            _ => throw new InvalidOperationException($"Unsupported control command '{control.Command}'."),
        };
    }

    public async Task<SustainTickResult> AdvanceDayAsync(string goalId, CancellationToken cancellationToken = default) =>
        await _scheduler.AdvanceDayAsync(goalId, (date, token) => SustainTickAsync(goalId, date, token), cancellationToken);

    private async Task<IReadOnlyList<object>> AdvanceDayControlAsync(Control control, CancellationToken cancellationToken)
    {
        var result = await AdvanceDayAsync(control.GoalId, cancellationToken);
        return result.Proposal is null ? [result.Status] : [result.Status, result.Proposal];
    }

    private Task<IReadOnlyList<object>> ResetControlAsync(Control control)
    {
        _resetData?.Invoke();
        if (_clock is VirtualClock virtualClock)
        {
            virtualClock.Reset(_clockAnchor);
        }

        _activeDispatch = null;
        _activePlan = null;
        _adaptationSequence = 0;
        var status = new StatusMessage
        {
            GoalId = control.GoalId,
            CorrelationId = $"reset-{_clock.Today:yyyyMMdd}",
            TaskStatus = TaskStatuses.Done,
            Payload = new StatusPayload
            {
                Day = DayName(_clock.Today),
                SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                Note = "reset complete; seed data restored and virtual clock returned to anchor",
                Material = false,
            },
        };
        return Task.FromResult<IReadOnlyList<object>>([status]);
    }

    private async Task<SustainTickResult> SustainTickAsync(
        string goalId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var dispatch = _activeDispatch ?? throw new InvalidOperationException("No active dispatch; run planning before sustain.");
        var plan = _activePlan ?? throw new InvalidOperationException("No active plan; run planning before sustain.");
        var world = await _grounding.AssembleAsync(dispatch, cancellationToken);
        var day = DayName(date);
        var planItem = plan.Plan.FirstOrDefault(item => string.Equals(item.Day, day, StringComparison.OrdinalIgnoreCase));
        var eventsToday = world.Calendar
            .Where(evt => DateOnly.TryParse(evt.Date, out var eventDate) && eventDate == date)
            .ToArray();

        var task = new GoalTask
        {
            GoalId = dispatch.GoalId,
            Contract = dispatch,
            Status = TaskStatuses.Monitoring,
            CreatedAt = world.AsOf,
            ActivePlan = plan.Plan,
        };

        MaterialityVerdict? material = null;
        WorldChange? materialChange = null;
        foreach (var evt in eventsToday)
        {
            var change = new WorldChange
            {
                Source = "calendar",
                Kind = "updated",
                Summary = $"calendar: {evt.Title} {day} {evt.Start} - prep window shrinks",
                ObservedAt = _clock.Now,
                EventDate = evt.Date,
                EventStart = evt.Start,
                EventEnd = evt.End,
                PlannedDay = day,
                PlannedDish = planItem?.Dish,
            };
            var verdict = _changeWatcher.Evaluate(change, task, world);
            if (verdict.IsMaterial)
            {
                material = verdict;
                materialChange = change;
                break;
            }
        }

        var isMaterial = material?.IsMaterial == true;
        var proposal = isMaterial && materialChange is not null
            ? await OnMaterialChangeAsync(goalId, materialChange, cancellationToken)
            : null;
        var status = new StatusMessage
        {
            GoalId = goalId,
            CorrelationId = $"sustain-{date:yyyyMMdd}",
            TaskStatus = TaskStatuses.Monitoring,
            Payload = new StatusPayload
            {
                Day = day,
                SimDate = date.ToString("yyyy-MM-dd"),
                Note = SustainNote(date, day, plan, world, isMaterial),
                Material = isMaterial,
            },
        };

        _trace.Record(new TraceEvent
        {
            At = _clock.Now,
            GoalId = goalId,
            Phase = TracePhase.Sustain,
            Source = nameof(Pipeline),
            Kind = "status_ready",
            Message = $"{day} sustain tick material={isMaterial.ToString().ToLowerInvariant()}",
        });

        return new SustainTickResult
        {
            Status = status,
            Proposal = proposal,
        };
    }

    private static string SustainNote(
        DateOnly date,
        string day,
        CandidatePlan plan,
        WorldState world,
        bool material)
    {
        var tomorrow = date.AddDays(1);
        var tomorrowDay = DayName(tomorrow);
        var tomorrowDish = plan.Plan.FirstOrDefault(item =>
            string.Equals(item.Day, tomorrowDay, StringComparison.OrdinalIgnoreCase))?.Dish;
        var expiring = world.ExpiringSoon
            .Where(item => DateOnly.TryParse(item.ExpiresOn, out var expires) && expires <= date.AddDays(1))
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (material)
        {
            return "calendar conflict overlaps the planned dinner prep window; adaptation proposed";
        }

        var reminder = tomorrowDish is null
            ? "on track"
            : $"on track; reminder set for {tomorrowDay} {tomorrowDish}";
        return expiring.Length == 0
            ? reminder
            : $"reconciled inventory ({string.Join(", ", expiring)} soon); {reminder}";
    }

    private static string DayName(DateOnly date) =>
        date.DayOfWeek switch
        {
            DayOfWeek.Monday => "Mon",
            DayOfWeek.Tuesday => "Tue",
            DayOfWeek.Wednesday => "Wed",
            DayOfWeek.Thursday => "Thu",
            DayOfWeek.Friday => "Fri",
            DayOfWeek.Saturday => "Sat",
            _ => "Sun",
        };

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
