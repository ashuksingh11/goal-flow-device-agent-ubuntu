using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using GoalFlow.Device.Products.FamilyHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace GoalFlow.Device.Agent;

/// <summary>LLM endpoint settings (OpenRouter, OpenAI-compatible) loaded from .env.</summary>
public sealed record AgentSettings
{
    /// <summary>OPENROUTER_API_KEY.</summary>
    public required string ApiKey { get; init; }

    /// <summary>OPENROUTER_BASE_URL, default https://openrouter.ai/api/v1.</summary>
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";

    /// <summary>OPENROUTER_MODEL, default openai/gpt-oss-120b.</summary>
    public string ModelId { get; init; } = "openai/gpt-oss-120b";
}

/// <summary>
/// HARNESS MODULE: Planner (+ Actuator) — the device IS an SK agent.
/// Hosts the Semantic Kernel: capability plugins registered as tools, the
/// SafetyFilter in the invocation pipeline, and OpenRouter as the
/// OpenAI-compatible chat model. Planning is ONE streaming chat invocation
/// with <c>FunctionChoiceBehavior.Auto</c> for grounding, followed by a
/// no-tools JSON-mode compose call. The LLM decides which [KernelFunction]s to
/// call, the kernel invokes them (through the filter), and this class narrates
/// everything as <c>agent_event</c> frames via Trace. LLM-ONLY: there is no
/// rules/scripted fallback.
/// </summary>
public sealed class GoalAgent
{
    private readonly Kernel _kernel;
    private readonly Trace _trace;
    private readonly Grounding _grounding;
    private readonly SafetyFilter _safety;
    private readonly ApprovalCoordinator _approvals;
    private readonly MonitorAdapt _monitor;
    private readonly CapabilityManager _capabilities;
    private readonly PrecheckEngine _prechecks;
    private readonly IClock _clock;
    private readonly ILogger<GoalAgent> _logger;
    private readonly TaskManager _tasks;

    /// <summary>Plan patches awaiting approval, keyed by proposal id → (goalId, patch).
    /// Registered when a daily adaptation is proposed; applied in ApplyApproval.
    /// CONCURRENT: two goals can be adapting at once, and Program dispatches every
    /// frame on its own Task.Run — a plain Dictionary tears under that.</summary>
    private readonly ConcurrentDictionary<string, (string GoalId, PlanPatch Patch)> _pendingPatches = new(StringComparer.Ordinal);

    /// <summary>
    /// Only ONE goal may be in the planning passes at a time.
    ///
    /// <para>
    /// Not a correctness fix — the state is per-goal now — but an honesty one. Three
    /// concurrent plans mean three simultaneous grounding tool-loops and composes:
    /// triple the token burn on a key that already 402s on low credit, and a
    /// "watch it think" stream interleaved from three goals that reads as noise.
    /// Serialising planning makes a queued goal show up on the board as WAITING —
    /// a visible state a person can understand — instead of a hidden stall.
    /// </para>
    ///
    /// <para>
    /// Approvals, control ticks and adaptations do NOT take this: they are short,
    /// and blocking them behind someone else's 60-second plan would be the very
    /// stall this avoids.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _planningSlot = new(1, 1);

    /// <summary>Marker module/function on an adaptation ProposalItem meaning "apply
    /// the pending plan patch" — intercepted in ApplyApproval, never kernel-invoked.</summary>
    private const string PlanPatchModule = "Plan";
    private const string PlanPatchFunction = "ApplyPatch";

    public GoalAgent(
        Kernel kernel,
        Trace trace,
        Grounding grounding,
        SafetyFilter safety,
        ApprovalCoordinator approvals,
        MonitorAdapt monitor,
        CapabilityManager capabilities,
        TaskManager tasks,
        PrecheckEngine prechecks,
        IClock clock,
        ILogger<GoalAgent> logger)
    {
        _kernel = kernel;
        _trace = trace;
        _grounding = grounding;
        _safety = safety;
        _approvals = approvals;
        _monitor = monitor;
        _capabilities = capabilities;
        _tasks = tasks;
        _prechecks = prechecks;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Builds the device kernel:
    ///   1. OpenRouter chat completion (OpenAI-compatible connector; model
    ///      <see cref="AgentSettings.ModelId"/>, endpoint <see cref="AgentSettings.BaseUrl"/>).
    ///   2. Capability plugins from the CapabilityManager's descriptors — i.e.
    ///      whatever the product pack registered, in ITS order (which is the
    ///      order the model sees its tools in). This method names no plugin type.
    ///   3. The <see cref="SafetyFilter"/> as an <see cref="IFunctionInvocationFilter"/>
    ///      service — every auto-invoked function passes through it.
    /// </summary>
    public static Kernel BuildKernel(AgentSettings settings, IServiceProvider services)
    {
        var builder = Kernel.CreateBuilder();

        builder.AddOpenAIChatCompletion(
            modelId: settings.ModelId,
            endpoint: new Uri(settings.BaseUrl),
            apiKey: settings.ApiKey);

        builder.Services.AddSingleton(services.GetRequiredService<ILoggerFactory>());
        builder.Services.AddSingleton<IFunctionInvocationFilter>(services.GetRequiredService<SafetyFilter>());
        builder.Services.AddSingleton(services.GetRequiredService<IProductApiAdapter>());

        foreach (var capability in services.GetRequiredService<CapabilityManager>().Descriptors)
        {
            builder.Plugins.AddFromObject(capability.Instance, capability.Name);
        }

        return builder.Build();
    }

    /// <summary>
    /// Runs one dispatch to a plan, STREAMING agent_events throughout:
    ///   phase(grounding)  → Grounding.AssembleAsync; SafetyFilter.BeginGoal(goal, constraints.hard)
    ///   phase(planning)   → compose the final JSON plan without tools;
    ///                       thinking/tool_call/tool_result/plan_progress events still flow
    ///   phase(checking)   → collect SafetyFilter verdict + freeze side-effects into
    ///                       tiered proposals via ApprovalCoordinator
    ///   phase(awaiting_approval) → return the plan_ready frame.
    /// </summary>
    public async Task<PlanReady> RunAsync(Dispatch dispatch, CancellationToken ct = default)
    {
        // Planning is serialised (see _planningSlot). If someone else holds the slot
        // this goal is QUEUED, and it says so rather than going quiet: the board
        // shows Waiting, which is a state a person can read, instead of a card that
        // sits there doing nothing for a minute.
        if (!await _planningSlot.WaitAsync(0, ct))
        {
            using var queuedScope = _trace.BeginGoalScope(dispatch.GoalId, dispatch.CorrelationId);
            _logger.LogInformation("plan_queued {GoalId} — another goal is planning", dispatch.GoalId);
            await _trace.PhaseAsync(Phases.Queued);
            await _trace.ThinkingAsync("Another goal is planning right now — I'll start on this one next.");
            await _planningSlot.WaitAsync(ct);
        }

        try
        {
            return await RunCoreAsync(dispatch, ct);
        }
        finally
        {
            _planningSlot.Release();
        }
    }

    /// <summary>
    /// Applies an approval frame: ApprovalCoordinator flips decisions, then the
    /// Actuator half executes each cleared proposal by invoking its frozen
    /// {module}.{function}(args) through the kernel (filter still applies),
    /// idempotently (MarkExecuted). Returns a status frame.
    /// </summary>
    public Task<Status> ApplyApprovalAsync(Approval approval, CancellationToken ct = default)
        => ApplyApprovalCoreAsync(approval, ct);

    /// <summary>
    /// Handles a control frame against the GENERIC clock: set_date/advance_day
    /// drive the SimulatedClock, reset restores the mock world; afterwards
    /// MonitorAdapt observes the (re-anchored) world and may yield an
    /// adaptation <see cref="Proposal"/> alongside the status.
    /// </summary>
    public Task<(Status Status, Proposal? Adaptation)> HandleControlAsync(Control control, CancellationToken ct = default)
        => HandleControlCoreAsync(control, ct);

    /// <summary>
    /// A WORLD-level clock command (no goal_id): advance the GLOBAL clock EXACTLY ONCE,
    /// then fan out over every active goal — observe each, emit its status (+ an
    /// adaptation proposal when a material change is newly surfaced) — and summarise the
    /// day's world events as one <see cref="DayAdvanced"/>. The clock is device-wide, so
    /// one tick moves the whole world; the per-goal machinery (each <see cref="GoalRecord"/>
    /// owns its snapshot + <c>EmittedMaterialChanges</c> dedup) already exists.
    /// </summary>
    public async Task<ControlResult> HandleWorldControlAsync(Control control, CancellationToken ct = default)
    {
        // Advance the one global clock exactly once.
        if (_clock is SimulatedClock sim)
        {
            if (control.Command == ControlCommands.SetDate && control.Payload?.Date is { } date)
                sim.SetDate(date);
            else if (control.Command == ControlCommands.AdvanceDay)
                sim.AdvanceDay();
        }

        if (control.Command == ControlCommands.Reset)
        {
            var store = _kernel.Services.GetRequiredService<IProductApiAdapter>();
            await store.ResetAsync(ct);
            // A world reset clears every goal for a clean slate.
            foreach (var g in _tasks.ActiveGoals)
            {
                _tasks.RemoveGoal(g.Dispatch.GoalId);
                _safety.RemoveGoal(g.Dispatch.GoalId);
            }
            return new ControlResult(
                Array.Empty<Status>(),
                Array.Empty<Proposal>(),
                new DayAdvanced { SimDate = _clock.Today.ToString("yyyy-MM-dd"), Day = 0, Events = [] });
        }

        var statuses = new List<Status>();
        var proposals = new List<Proposal>();
        var events = new Dictionary<string, DayEvent>(StringComparer.Ordinal);

        foreach (var goal in _tasks.ActiveGoals)
        {
            var goalId = goal.Dispatch.GoalId;
            using var scope = _trace.BeginGoalScope(goalId, goal.Dispatch.CorrelationId);
            using var policy = _safety.EnterGoal(goalId);

            // A monitored goal finishes when its window closes — the calendar decides, not the agent.
            if (await CompleteIfWindowPassedAsync(goal, ct))
            {
                statuses.Add(BuildMonitoringStatus(goalId, goal.Dispatch.CorrelationId, false,
                    $"goal complete — its time window closed on {goal.Dispatch.TimeWindow?.End}.{ProgressNote(goal)}", null)
                    with { TaskStatus = TaskStatuses.Done });
                continue;
            }

            var changes = await _monitor.ObserveAsync(goal, ct);
            var material = changes.FirstOrDefault(c => c.Material && goal.EmittedMaterialChanges.Add(c.Key));
            if (material is not null)
            {
                await _trace.PhaseAsync("adapting");
                var proposal = material.Steer is not null
                    ? await ProposeDailyAdaptationAsync(goalId, goal, material, ct)
                    : await _monitor.ProposeAdaptationAsync(goalId, material, ct);
                if (proposal is not null)
                {
                    proposals.Add(proposal with { CorrelationId = goal.Dispatch.CorrelationId });
                }
                statuses.Add(BuildMonitoringStatus(goalId, goal.Dispatch.CorrelationId, true, $"material: {material.Description}"));
                AddDayEvent(events, material, goalId);
            }
            else
            {
                var quiet = changes.Any(c => c.Material)
                    ? "on track; material change already surfaced for approval."
                    : "on track; no material world changes affect the active plan.";
                statuses.Add(BuildMonitoringStatus(goalId, goal.Dispatch.CorrelationId, false, quiet));
            }
        }

        var summary = new DayAdvanced
        {
            SimDate = _clock.Today.ToString("yyyy-MM-dd"),
            Day = ComputeSimDay(),
            Events = events.Values.ToArray()
        };
        return new ControlResult(statuses, proposals, summary);
    }

    /// <summary>Record a material world change against the day summary, merging goal_ids across goals.</summary>
    private static void AddDayEvent(Dictionary<string, DayEvent> events, WorldChange change, string goalId)
    {
        if (events.TryGetValue(change.Key, out var existing))
        {
            events[change.Key] = existing with { GoalIds = [.. existing.GoalIds, goalId] };
            return;
        }
        events[change.Key] = new DayEvent
        {
            Id = change.Key,
            Title = change.Description,
            Kind = change.Kind,
            Summary = change.Description,
            GoalIds = [goalId]
        };
    }

    /// <summary>1-based sim day, measured from the earliest active goal's window start.</summary>
    private int ComputeSimDay()
    {
        var starts = _tasks.ActiveGoals
            .Select(g => DateOnly.TryParse(g.Dispatch.TimeWindow?.Start, out var s) ? s : (DateOnly?)null)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToArray();
        if (starts.Length == 0) return 0;
        return Math.Max(1, _clock.Today.DayNumber - starts.Min().DayNumber + 1);
    }

    private const int MaxComposeAttempts = 3;

    /// <summary>
    /// How long ONE provider call may take before it is treated as a hang.
    ///
    /// <para>
    /// Generous on purpose — a healthy call to this model answers in seconds, so these
    /// are not latency targets. They are the line past which "slow" becomes "never":
    /// the alternative is what shipped before them, a goal wedged for four hours while
    /// the board reported progress. Streaming gets more room because it runs the tool
    /// loop and reasons aloud. See <see cref="Deadline"/>.
    /// </para>
    /// </summary>
    private static readonly TimeSpan LlmCallBudget = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StreamingCallBudget = TimeSpan.FromSeconds(150);

    /// <summary>Id of the one task a goal falls back to when decomposition is unavailable.</summary>
    private const string SingleTaskId = "t1";

    /// <summary>
    /// THE FALL-BACK: one task for the whole goal — exactly the v2 shape. Used
    /// when decomposition fails or the model returns nothing usable. The goal
    /// still plans and still runs; the board just shows one coarse step instead of
    /// several. A decomposition failure must never cost the user their goal.
    /// </summary>
    private static IReadOnlyList<TaskRecord> SynthesizeTasks(Dispatch dispatch)
        => [new TaskRecord { TaskId = SingleTaskId, GoalId = dispatch.GoalId, Title = dispatch.Objective }];

    /// <summary>
    /// The goal can't be planned yet: the world isn't ready. It WAITS — an empty
    /// plan with the reason and a `waiting` status — rather than failing.
    ///
    /// <para>
    /// The distinction is the component's whole point, and it is a distinction the
    /// user feels. "Blocked by safety" means never. "Waiting for approval" means
    /// the user must act. "Waiting on a precheck" means something in the house is
    /// unplugged: nobody did anything wrong, and it will resume by itself. Saying
    /// which one is why the remediation text exists.
    /// </para>
    /// </summary>
    private PlanReady BuildPrecheckBlockedPlan(Dispatch dispatch, PrecheckReport precheck)
    {
        _logger.LogInformation("plan_precheck_blocked {GoalId}: {Remediation}", dispatch.GoalId, precheck.Remediation);
        return new PlanReady
        {
            GoalId = dispatch.GoalId,
            CorrelationId = dispatch.CorrelationId,
            // Not "blocked": nothing is wrong with the goal, and it should be
            // retried when the world recovers.
            TaskStatus = TaskStatuses.Monitoring,
            Payload = new PlanReadyPayload
            {
                Plan = [],
                Proposals = [],
                Safety = new SafetyVerdict { Gate = SafetyGates.Passed, Violations = [] },
                Precheck = ToPrecheckVerdict(precheck),
                Impact = [],
                Explanation = $"I can't start this yet — {precheck.Remediation}. I'll pick it up once that's sorted."
            }
        };
    }

    /// <summary>The wire shape of a precheck report (plan_ready.payload.precheck).</summary>
    private static PrecheckVerdict ToPrecheckVerdict(PrecheckReport report)
        => new()
        {
            Ok = report.Ok,
            Results = report.Results
                .Select(r => new PrecheckResultDto
                {
                    Id = r.Id,
                    Status = r.Status.ToString().ToLowerInvariant(),
                    Detail = r.Detail
                })
                .ToArray()
        };

    /// <summary>
    /// Moves every task currently in <paramref name="from"/> to <paramref name="to"/>.
    ///
    /// <para>
    /// The compose plans the whole goal at once, so its tasks move as a cohort
    /// rather than one at a time. Filtering on the CURRENT state matters: a task
    /// that already failed or was cancelled must not be dragged forward, and the
    /// ledger would refuse the illegal move anyway — this just doesn't ask.
    /// </para>
    /// </summary>
    private async Task AdvanceTasksAsync(string goalId, TaskState from, TaskState to)
    {
        var goal = _tasks.GetGoal(goalId);
        if (goal is null)
        {
            return;
        }

        foreach (var task in goal.Tasks.Where(t => t.State == from).ToArray())
        {
            await _tasks.TransitionAsync(goalId, task.TaskId, to);
        }
    }

    /// <summary>
    /// Completes a goal's monitoring tasks once its time window has closed.
    ///
    /// <para>
    /// Without this a goal monitors forever: the board would show a finished meal
    /// week stuck at "in progress" all year. The end condition is the contract's
    /// own <c>time_window.end</c> read against the GENERIC clock — not a guess, and
    /// not the agent deciding for itself that it is finished.
    /// </para>
    ///
    /// <para>Returns true when this call is what completed it.</para>
    /// </summary>
    private async Task<bool> CompleteIfWindowPassedAsync(GoalRecord goal, CancellationToken ct)
    {
        // A plan is complete once the clock passes its LAST DAY. Derive that from the
        // plan's OWN day span (Day 1..N, anchored at the dispatch start) rather than the
        // LLM's dispatch-window end — so completion lines up with the board's day-by-day
        // progress reaching 100%. Fall back to the dispatch window end when there is no plan.
        DateOnly lastDay;
        if (DateOnly.TryParse(goal.Dispatch.TimeWindow?.Start, out var start) && goal.Plan.Count > 0)
        {
            var maxDay = Math.Max(1, goal.Plan.Max(p => p.Day));
            lastDay = start.AddDays(maxDay - 1);
        }
        else if (!DateOnly.TryParse(goal.Dispatch.TimeWindow?.End, out lastDay))
        {
            return false;
        }

        if (_clock.Today <= lastDay)
        {
            return false;
        }

        var monitoring = goal.Tasks.Where(t => t.State == TaskState.Monitoring).ToArray();
        if (monitoring.Length == 0)
        {
            return false;
        }

        foreach (var task in monitoring)
        {
            await _tasks.TransitionAsync(goal.Dispatch.GoalId, task.TaskId, TaskState.Completed);
        }

        _logger.LogInformation("goal_complete {GoalId} last_day={LastDay} progress={Progress}%",
            goal.Dispatch.GoalId, lastDay, goal.ProgressPercent);
        return true;
    }

    /// <summary>
    /// The one-line progress summary — what Agent Board will render as a
    /// percentage and a next step. Derived from the ledger, never from the clock.
    /// </summary>
    private static string ProgressNote(GoalRecord? goal)
        => goal is null || goal.Tasks.Count <= 1
            ? ""
            : $" Goal {goal.ProgressPercent}% ({goal.WorkDone}/{goal.Tasks.Count} steps done).";

    /// <summary>
    /// ALTITUDE ONE of the planner: what are the pieces of this goal?
    ///
    /// <para>
    /// A JSON-mode call with NO tools, over the capabilities the device actually
    /// advertises, asking only for structure — titles and dependencies — never
    /// world facts. Grounding is altitude two's job (that pass has the tools);
    /// asking a toolless model about the fridge would just invite invention.
    /// </para>
    ///
    /// <para>
    /// The result is a suggestion: <see cref="TaskDag.Sanitize"/> repairs it into
    /// something executable. FAIL-SOFT everywhere — any failure returns the single
    /// synthesized task rather than throwing, because a goal that plans coarsely
    /// beats a goal that dies.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<TaskRecord>> DecomposeAsync(IChatCompletionService chat, Dispatch dispatch, CancellationToken ct)
    {
        try
        {
            var history = new ChatHistory();
            history.AddSystemMessage(DecomposeSystemPrompt);
            history.AddUserMessage(BuildDecomposeInstruction(dispatch));

            // RETRY transient provider errors, like every other LLM call here.
            // Fail-soft is the LAST resort, not the first response to a known-flaky
            // provider: OpenRouter regularly returns finish_reason=error mid-stream
            // ("Unknown ChatFinishReason value"), and without this a single hiccup
            // permanently collapsed the goal to one task — the board would show 1
            // step instead of 7 at random. Retrying is safe: decompose has no tools
            // and no side effects.
            string content = "";
            for (var attempt = 1; attempt <= MaxComposeAttempts; attempt++)
            {
                try
                {
                    content = await GetComposeContentAsync(chat, history, ct);
                    break;
                }
                catch (Exception ex) when (attempt < MaxComposeAttempts && IsTransientProviderError(ex, ct))
                {
                    _logger.LogWarning("decompose_transient attempt {Attempt}/{Max}: {Message}; retrying", attempt, MaxComposeAttempts, ex.Message);
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
                }
            }

            var json = ExtractJson(content);
            if (json is null)
            {
                _logger.LogWarning("decompose_unparseable — falling back to a single task");
                return SynthesizeTasks(dispatch);
            }

            var proposed = JsonSerializer.Deserialize<DecomposeResult>(json, ContractJson.Options)?.Tasks;
            if (proposed is null || proposed.Count == 0)
            {
                _logger.LogWarning("decompose_empty — falling back to a single task");
                return SynthesizeTasks(dispatch);
            }

            var (tasks, repairs) = TaskDag.Sanitize(proposed
                .Select((t, i) => new TaskRecord
                {
                    TaskId = string.IsNullOrWhiteSpace(t.Id) ? $"t{i + 1}" : t.Id,
                    GoalId = dispatch.GoalId,
                    Title = t.Title ?? dispatch.Objective,
                    DependsOn = t.DependsOn ?? [],
                    Capabilities = t.Capabilities ?? []
                })
                .ToArray());

            foreach (var repair in repairs)
            {
                _logger.LogWarning("decompose_repaired {Repair}", repair);
            }

            _logger.LogInformation("decomposed {GoalId} into {Count} task(s){Repaired}",
                dispatch.GoalId, tasks.Count, repairs.Count > 0 ? $" ({repairs.Count} repaired)" : "");
            await _trace.ThinkingAsync($"broke the goal into {tasks.Count} steps: {string.Join(" → ", tasks.Select(t => t.Title))}");
            return tasks;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "decompose_failed — falling back to a single task");
            return SynthesizeTasks(dispatch);
        }
    }

    private const string DecomposeSystemPrompt = """
        You break a home-assistant goal into the few steps needed to achieve it.
        Reply with JSON only (no prose, no Markdown, no code fence):
        { "tasks": [ { "id": "t1", "title": "short imperative step", "depends_on": [], "capabilities": ["Inventory"] } ] }

        Rules:
        - Between 1 and 8 tasks. Fewer, meaningful steps beat many trivial ones.
        - Order them so dependencies come first; depends_on lists ids from THIS list only.
        - A task is a unit of work worth showing a person as "next step" — not a tool call.
        - capabilities: which listed modules the step will likely use. Advisory.
        - Do NOT state world facts (what is in the fridge, who is busy). You cannot
          see the world here; a later pass grounds each step against it.
        """;

    /// <summary>The decompose instruction: the contract + what this device can actually do.</summary>
    private string BuildDecomposeInstruction(Dispatch dispatch)
        => $"""
        Task Contract:
        {ContractJson.Serialize(dispatch)}

        Capabilities available on this device:
        {string.Join("\n", _capabilities.Descriptors.Where(d => d.Available).Select(d => $"- {d.Name}"))}
        """;

    /// <summary>The decompose call's wire shape (snake_case via ContractJson).</summary>
    private sealed record DecomposeResult
    {
        public List<DecomposedTask> Tasks { get; init; } = [];
    }

    private sealed record DecomposedTask
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public List<string>? DependsOn { get; init; }
        public List<string>? Capabilities { get; init; }
    }

    /// <summary>The grounding instruction (user message) rendered from the contract.</summary>
    internal static string BuildGroundingInstruction(Dispatch dispatch)
        => $$"""
        Task Contract:
        {{ContractJson.Serialize(dispatch)}}

        Grounding rules:
        - This is LLM-only planning. Use Semantic Kernel read-only tools for grounding; do not invent inventory, calendar, recipe, reminder, or shopping-list facts.
        - Call these read tools when relevant: Inventory.ListItems, Inventory.GetExpiringItems, Calendar.GetBusyEvenings, Calendar.GetEvents, Recipes.FindRecipes, ShoppingList.GetList, Reminders.List, Guests.GetEvent, Guests.GetGuests, Guests.GetDietaryConstraints, Appliance.ListAppliances.
        - Guest tools are relevant only when the contract domain/objective/scope/context mentions guests, hosting, RSVPs, or a dinner party. Do not use guest data for an ordinary meal_plan goal.
        - For domain guest_dinner, ground guests, dietary constraints, appliance state, recipes, inventory, calendar, shopping list, and reminders; Appliance.ListAppliances is the read-only source for oven/dishwasher/fridge availability.
        - During planning side effects are intentionally not exposed as tools.
        - Do not produce the final plan yet.
        - Return a concise grounding summary of the facts, constraints, candidate recipes, missing items, and scheduling context that the final plan must use.
        """;

    /// <summary>The final no-tools compose instruction rendered from the contract.</summary>
    internal static string BuildPlanningInstruction(Dispatch dispatch)
        => $$"""
        Task Contract:
        {{ContractJson.Serialize(dispatch)}}

        Compose rules:
        - Use the grounded facts and tool results already present in this conversation. Do not call tools in this step.
        - During planning side effects are intentionally not exposed as tools. Propose mutations in the final JSON instead.
        - Proposal module/function/args must match real side-effecting functions exactly.
        - Valid side-effecting guest-dinner proposal functions include:
          ShoppingList.Add args {"items":["..."],"reason":"..."}
          ShoppingList.PlaceOrder args {"estimatedTotal":42.50}
          Appliance.PreheatOven args {"targetC":180,"atTime":"YYYY-MM-DDTHH:mm"}
          Appliance.RunProgram args {"appliance":"dishwasher","program":"eco","atTime":"YYYY-MM-DDTHH:mm"}
          Appliance.Defrost args {"item":"...","atTime":"YYYY-MM-DDTHH:mm"}
          Reminders.Create args {"title":"...","date":"YYYY-MM-DD","time":"HH:mm"}
        - Do not invent proposal functions such as Appliance.Preheat, Reminders.Add, or Reminder.Create.
        - For meal_plan goals, produce EXACTLY 7 dinner plan items for a one-week plan — Day 1 through Day 7 — no more and no fewer. Do not tie the count to any dates.
        - For guest_dinner, include a menu that honors guest dietary constraints, a prep timeline whose plan item "when" values include times where useful (YYYY-MM-DDTHH:mm), shopping proposals for missing ingredients, appliance prep proposals, and reminders.
        - For guest_dinner appliance prep, prefer concrete proposals when grounded appliances support them: Appliance.PreheatOven before an oven-warmed dish, Appliance.RunProgram for dishwasher cleanup before quiet_hours, and Appliance.Defrost only when a frozen item needs thawing.
        - Do not propose ingredients or recipes that violate hard constraints.
        - Propose AT MOST 5 side-effecting actions. NEVER emit duplicate proposals. Consolidate a
          recurring action (e.g. a nightly dishwasher run) into ONE proposal, not one per night. Keep
          the plan tight — fewer, higher-value proposals.
        - Use ISO dates inside the contract time_window. Never use a hardcoded anchor date.
        - The response must start with { and end with }. Do not output whitespace, Markdown, code fences, or prose outside the JSON object.

        Final answer must be only valid JSON with this shape:
        {
          "plan": [
            {"id":"s1","title":"...","detail":"...","when":"YYYY-MM-DD or YYYY-MM-DDTHH:mm","why":["..."],"tags":["..."]}
          ],
          "proposals": [
            {"proposal_id":"p1","action":"add missing groceries","module":"ShoppingList","function":"Add","args":{"items":["..."],"reason":"..."},"tier":"light","reason":"...","requires_approval":true},
            {"proposal_id":"p2","action":"place grocery order","module":"ShoppingList","function":"PlaceOrder","args":{"estimatedTotal":42.50},"tier":"firm","reason":"...","requires_approval":true}
          ],
          "impact": [{"label":"waste","value":"uses 2 expiring items"}],
          "explanation": "one concise paragraph"
        }
        """;

    private static string BuildPlanningRetryInstruction(string error)
        => $$"""
        The previous compose response could not be parsed as the required plan JSON.
        Parser error: {{error}}

        Return ONLY the JSON plan object matching this schema, no prose, no Markdown, no code fence:
        {
          "plan": [
            {"id":"s1","title":"...","detail":"...","when":"YYYY-MM-DD","why":["..."],"tags":["..."]}
          ],
          "proposals": [
            {"proposal_id":"p1","action":"...","module":"ShoppingList","function":"Add","args":{"items":["..."],"reason":"..."},"tier":"light","reason":"...","requires_approval":true}
          ],
          "impact": [{"label":"...","value":"..."}],
          "explanation": "one concise paragraph"
        }
        """;

    private async Task<PlanReady> RunCoreAsync(Dispatch dispatch, CancellationToken ct)
    {
        using var scope = _trace.BeginGoalScope(dispatch.GoalId, dispatch.CorrelationId);
        // Arm THIS goal's hard constraints and enter its scope: every kernel call
        // made inside this async flow is checked against them and nothing else.
        // (Previously one shared field — a second goal overwrote it mid-plan.)
        using var policy = _safety.BeginGoal(dispatch.GoalId, dispatch.Constraints.Hard);
        _safety.SetTrace(_trace);

        _logger.LogInformation("plan_start domain={Domain} model_clock={Today}", dispatch.Domain, _clock.Today);
        // PRE-CHECK GATE 1: can this goal be planned at all? Before a single token
        // is spent. A plan built while signed out is a plan that cannot be
        // delivered, and finding that out at approval time wastes the user's
        // decision as well as the tokens.
        var precheck = await _prechecks.RunForDispatchAsync(dispatch, ct);
        if (!precheck.Ok)
        {
            return BuildPrecheckBlockedPlan(dispatch, precheck);
        }

        var chat = _kernel.Services.GetRequiredService<IChatCompletionService>();

        // ALTITUDE ONE: what are the pieces of this goal? Structure only, no tools,
        // no world facts — that is what grounding below is for. Fails soft to one
        // task, so the goal always plans.
        var taskDag = await DecomposeAsync(chat, dispatch, ct);

        await _trace.PhaseAsync("grounding");
        var ground = await _grounding.AssembleAsync(dispatch, _kernel, ct);

        var history = new ChatHistory();
        history.AddSystemMessage(_grounding.RenderPrompt(ground));
        history.AddUserMessage(BuildGroundingInstruction(dispatch));

        var groundingSettings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(GroundingFunctions(), autoInvoke: true, options: new FunctionChoiceBehaviorOptions
            {
                AllowConcurrentInvocation = false,
                AllowParallelCalls = false
            }),
            Temperature = 0.2,
            MaxTokens = 2500
        };

        var groundingSummary = await RunGroundingPassAsync(chat, history, groundingSettings, ct);

        if (groundingSummary.Length > 0)
        {
            history.AddAssistantMessage(groundingSummary.ToString());
        }

        await _trace.PhaseAsync("planning");
        var modelPlan = await ComposeModelPlanAsync(chat, history, dispatch, ct);
        modelPlan = modelPlan with { Plan = AssignPlanDays(modelPlan.Plan) };

        await _trace.PhaseAsync("checking");
        // Collapse duplicate proposals the model sometimes emits (e.g. the same
        // "run dishwasher" action repeated per night) — dedupe by module+function+args
        // so the UI shows one, and re-assign sequential proposal ids after collapsing.
        var proposals = modelPlan.Proposals
            .Select(NormalizeProposal)
            .GroupBy(p => $"{p.Module}|{p.Function}|{p.Args?.ToJsonString() ?? string.Empty}")
            .Select(g => g.First())
            .Select((p, i) => p with { ProposalId = $"p{i + 1}" })
            .Select(_approvals.Register)
            .ToArray();
        foreach (var item in modelPlan.Plan)
        {
            await _trace.PlanProgressAsync(item);
        }

        var worldSnapshot = await _monitor.CaptureSnapshotAsync(ct);
        // Whether this domain has fire-able events is the observer's business, not
        // a domain name this method has to recognise.
        var demoEvents = _monitor.DemoEventsFor(dispatch.Domain, worldSnapshot);

        var ready = new PlanReady
        {
            GoalId = dispatch.GoalId,
            CorrelationId = dispatch.CorrelationId,
            TaskStatus = TaskStatuses.AwaitingApproval,
            Payload = new PlanReadyPayload
            {
                Plan = modelPlan.Plan,
                Proposals = proposals,
                Safety = new SafetyVerdict { Gate = _safety.GateFor(dispatch.GoalId), Violations = _safety.ViolationsFor(dispatch.GoalId).ToArray() },
                Precheck = ToPrecheckVerdict(precheck),
                Impact = modelPlan.Impact,
                DemoEvents = demoEvents,
                Explanation = modelPlan.Explanation
            }
        };

        await _trace.PhaseAsync("awaiting_approval");
        var goal = _tasks.CreateGoal(dispatch, taskDag, worldSnapshot);
        goal.Plan = modelPlan.Plan;
        // The compose above planned the whole goal in one pass, so every task is
        // planned and waiting on the same approval. (Per-task planning — pulling
        // one task off the frontier at a time — is the next altitude; the DAG and
        // the ledger are what make it possible.)
        foreach (var task in goal.Tasks)
        {
            await _tasks.TransitionAsync(dispatch.GoalId, task.TaskId, TaskState.Ready);
            await _tasks.TransitionAsync(dispatch.GoalId, task.TaskId, TaskState.Planning);
            await _tasks.TransitionAsync(dispatch.GoalId, task.TaskId, TaskState.AwaitingApproval);
        }
        _logger.LogInformation("plan_ready items={ItemCount} proposals={ProposalCount} safety={Safety}", ready.Payload.Plan.Count, ready.Payload.Proposals.Count, ready.Payload.Safety.Gate);
        return ready;
    }

    /// <summary>
    /// The grounding pass: the model calls the read-only tools and narrates what it found.
    ///
    /// Wrapped in the SAME transient-provider retry as compose. Without it a single
    /// provider/SDK hiccup here (e.g. "Unknown ChatFinishReason value" when OpenRouter
    /// returns finish_reason=error mid-stream) killed the ENTIRE dispatch — the goal just
    /// died and the UI sat on "planning". Re-running is safe: this pass only invokes
    /// READ-ONLY functions.
    /// </summary>
    private async Task<StringBuilder> RunGroundingPassAsync(
        IChatCompletionService chat,
        ChatHistory history,
        OpenAIPromptExecutionSettings settings,
        CancellationToken ct)
    {
        var baseline = history.Count;

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                // Roll the history back to the pre-attempt state: a stream that died
                // mid-flight can leave a dangling tool-call with no result, which the
                // next request rejects.
                while (history.Count > baseline)
                {
                    history.RemoveAt(history.Count - 1);
                }
            }

            var summary = new StringBuilder();
            try
            {
                // The grounding pass calls tools and streams its reasoning, so it gets
                // the widest budget of any call here — but a budget nonetheless.
                using var cts = Deadline(ct, StreamingCallBudget);
                await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, _kernel, cts.Token))
                {
                    if (!string.IsNullOrEmpty(chunk.Content))
                    {
                        summary.Append(chunk.Content);
                        await _trace.ThinkingAsync(chunk.Content);
                    }
                }
                return summary;
            }
            // The final attempt's exception propagates (guard excludes it), so a real
            // failure still surfaces instead of looping.
            catch (Exception ex) when (attempt < MaxComposeAttempts && IsTransientProviderError(ex, ct))
            {
                var note = $"planner_notice: grounding attempt {attempt}/{MaxComposeAttempts} hit a transient provider error ({ex.GetType().Name}: {ex.Message}); retrying.";
                _logger.LogWarning(ex, "{Note}", note);
                await _trace.ThinkingAsync(note);
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }
        }
    }

    private async Task<ModelPlan> ComposeModelPlanAsync(IChatCompletionService chat, ChatHistory history, Dispatch dispatch, CancellationToken ct)
    {
        history.AddUserMessage(BuildPlanningInstruction(dispatch));
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxComposeAttempts; attempt++)
        {
            if (attempt > 1)
            {
                history.AddUserMessage(BuildPlanningRetryInstruction(lastError ?? "Planner did not return a valid JSON object."));
            }

            string raw;
            try
            {
                raw = await GetComposeContentAsync(chat, history, ct);
            }
            catch (Exception ex) when (IsTransientProviderError(ex, ct))
            {
                // The provider (OpenRouter) or the OpenAI SDK occasionally hiccups:
                // an unrecognized finish_reason the SDK can't deserialize, a 5xx/429,
                // a socket timeout. These are TRANSPORT flakiness, not a modelling
                // failure — retry the LLM (still LLM-only; no scripted plan). Genuine
                // cancellation is excluded by IsTransientProviderError and propagates.
                lastError = $"provider/transport error: {ex.Message}";
                var tnote = $"planner_notice: compose attempt {attempt}/{MaxComposeAttempts} hit a transient provider error ({ex.GetType().Name}: {ex.Message}); retrying.";
                _logger.LogWarning(ex, "{Note}", tnote);
                await _trace.ThinkingAsync(tnote);
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(raw))
            {
                await _trace.ThinkingAsync(raw);
                history.AddAssistantMessage(raw);
            }

            if (TryParseModelPlan(raw, out var modelPlan, out var error))
            {
                return modelPlan;
            }

            lastError = error;
            var note = $"planner_notice: compose attempt {attempt}/{MaxComposeAttempts} did not return parseable plan JSON: {error}";
            _logger.LogWarning("{Note}", note);
            await _trace.ThinkingAsync(note);
        }

        var failure = $"Planner failed to return a valid JSON plan after {MaxComposeAttempts} compose attempt(s). Last error: {lastError}";
        _logger.LogError("{Failure}", failure);
        await _trace.ThinkingAsync($"planner_error: {failure}");
        throw new JsonException(failure);
    }

    private async Task<string> GetComposeContentAsync(IChatCompletionService chat, ChatHistory history, CancellationToken ct)
    {
        var jsonSettings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.1,
            MaxTokens = 6000,
            ResponseFormat = "json_object"
        };

        try
        {
            using var cts = Deadline(ct, LlmCallBudget);
            var response = await chat.GetChatMessageContentAsync(history, jsonSettings, _kernel, cts.Token);
            var content = response.Content ?? "";
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            var note = "planner_notice: provider returned empty content with JSON response_format; retrying compose with strict JSON prompt only.";
            _logger.LogWarning("{Note}", note);
            await _trace.ThinkingAsync(note);
            return await GetStrictComposeContentAsync(chat, history, ct);
        }
        catch (Exception ex) when (LooksLikeResponseFormatRejection(ex))
        {
            var note = $"planner_notice: provider rejected JSON response_format; retrying compose with strict JSON prompt only. Error: {ex.Message}";
            _logger.LogWarning(ex, "{Note}", note);
            await _trace.ThinkingAsync(note);
            return await GetStrictComposeContentAsync(chat, history, ct);
        }
    }

    private async Task<string> GetStrictComposeContentAsync(IChatCompletionService chat, ChatHistory history, CancellationToken ct)
    {
        var strictSettings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.1,
            MaxTokens = 6000
        };
        using var cts = Deadline(ct, LlmCallBudget);
        var response = await chat.GetChatMessageContentAsync(history, strictSettings, _kernel, cts.Token);
        return response.Content ?? "";
    }

    // ---- Daily adaptation: a scoped, tokens-lean LLM re-plan of the affected slice.
    // Quiet days cost zero LLM calls (deterministic materiality gate); a material day
    // is ONE small call (no grounding tool-loop) that returns a minimal plan patch. --

    private const string AdaptSystemPrompt = """
        You keep an already-approved home plan in sync with the real world.
        A single world change just happened. Adapt ONLY the affected part of the
        plan — do not rewrite rows the change doesn't touch. Reply with a MINIMAL
        JSON patch and nothing else (no prose, no Markdown, no code fence):
        {
          "upsert": [ { "id": "<existing id to REPLACE, or a new id to ADD>", "day": 1, "title": "...", "detail": "...", "when": "YYYY-MM-DD", "why": ["short reason"] } ],
          "remove": ["<id to drop>"],
          "impact_delta": [ { "label": "waste", "value": "-2 items" } ],
          "rationale": "one sentence explaining the change"
        }
        Keep it tiny: usually a single upsert. Reuse an existing id to SWAP that row;
        use a new id only to ADD a step. Honor the steer.
        """;

    private async Task<Proposal?> ProposeDailyAdaptationAsync(string goalId, GoalRecord active, WorldChange change, CancellationToken ct, string? eventId = null)
    {
        var chat = _kernel.Services.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(AdaptSystemPrompt);
        history.AddUserMessage(BuildAdaptInstruction(active.Plan, change));

        PlanPatch? patch = null;
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxComposeAttempts; attempt++)
        {
            var raw = await GetAdaptContentAsync(chat, history, ct);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                await _trace.ThinkingAsync(raw);
                history.AddAssistantMessage(raw);
            }

            if (TryParsePlanPatch(raw, out var parsedPatch, out var error) && (parsedPatch.Upsert.Count > 0 || parsedPatch.Remove.Count > 0))
            {
                patch = NormalizeAdaptationPatch(active.Plan, change, parsedPatch);
                break;
            }

            lastError = string.IsNullOrWhiteSpace(error)
                ? "Patch had no upsert and no remove."
                : error;

            if (attempt < MaxComposeAttempts)
            {
                history.AddUserMessage("Your previous reply was not a complete JSON patch. Reply again with ONLY the minimal JSON patch object, complete and valid.");
            }
        }

        if (patch is null)
        {
            _logger.LogWarning("adaptation_patch_unusable kind={Kind}: {Error}", change.Kind, lastError);
            await _trace.ThinkingAsync($"planner_notice: adaptation produced no usable patch ({lastError}); leaving plan unchanged.");
            return null;
        }

        var proposalId = $"a{_approvals.All().Count(p => p.ProposalId.StartsWith('a')) + 1}";
        _approvals.Register(new ProposalItem
        {
            ProposalId = proposalId,
            Action = change.RecommendedAction ?? "adapt the plan",
            Module = PlanPatchModule,
            Function = PlanPatchFunction,
            Tier = ApprovalTiers.Adapt,
            Reason = patch.Rationale,
            RequiresApproval = true
        });
        _pendingPatches[proposalId] = (goalId, patch);
        _logger.LogInformation("adaptation_proposed {ProposalId} kind={Kind} upsert={Upsert} remove={Remove}", proposalId, change.Kind, patch.Upsert.Count, patch.Remove.Count);

        return new Proposal
        {
            GoalId = goalId,
            TaskStatus = TaskStatuses.Adapting,
            Payload = new AdaptationPayload
            {
                ProposalId = proposalId,
                Action = change.RecommendedAction ?? "adapt the plan",
                Detail = patch.Rationale,
                Trigger = change.Description,
                EventId = eventId,
                Tier = ApprovalTiers.Adapt,
                RequiresApproval = true,
                Patch = patch
            }
        };
    }

    private static string BuildAdaptInstruction(IReadOnlyList<PlanItem> plan, WorldChange change)
    {
        var planLines = string.Join("\n", plan.Select(p =>
            $"- Day {p.Day} | {p.Id} | {p.Title}{(p.Detail is null ? "" : " — " + p.Detail)}"));
        var context = change.Context is null ? "" : $"\nDetails: {change.Context.ToJsonString()}";
        var targetDay = change.TargetDay?.ToString() ?? "the affected";
        var targetId = change.TargetItemId ?? change.AffectedPlanItems.FirstOrDefault() ?? "the affected row id";
        var targetTitle = change.TargetTitle ?? "unknown";
        return $"""
            CURRENT PLAN (day | id | title):
            {planLines}

            WORLD CHANGE: {change.Description}{context}
            HOW TO ADAPT: {change.Steer}

            The Day {targetDay} dinner is currently '{targetTitle}' (id={targetId}).
            Change ONLY the Day {targetDay} dinner to honor the world change and steer.
            Return a patch whose `upsert` REUSES the exact id `{targetId}` so it replaces that row.
            Do not change, remove, reorder, or retitle any other day.
            """;
    }

    private static PlanPatch NormalizeAdaptationPatch(IReadOnlyList<PlanItem> plan, WorldChange change, PlanPatch patch)
    {
        if (change.TargetItemId is null || patch.Upsert.Count == 0)
        {
            return patch with { Upsert = AssignPatchDays(plan, patch.Upsert) };
        }

        var target = plan.FirstOrDefault(item => string.Equals(item.Id, change.TargetItemId, StringComparison.Ordinal));
        if (target is null)
        {
            return patch with { Upsert = AssignPatchDays(plan, patch.Upsert) };
        }

        var targetDay = target.Day > 0 ? target.Day : change.TargetDay ?? 0;
        var upsert = patch.Upsert
            .Take(1)
            .Select(row => row with
            {
                Id = target.Id,
                Day = targetDay
            })
            .ToArray();

        return patch with
        {
            Upsert = upsert,
            Remove = []
        };
    }

    private static IReadOnlyList<PlanItem> AssignPatchDays(IReadOnlyList<PlanItem> plan, IReadOnlyList<PlanItem> upsert)
        => upsert.Select(row =>
        {
            var existing = plan.FirstOrDefault(item => string.Equals(item.Id, row.Id, StringComparison.Ordinal));
            if (row.Day > 0 || existing is null)
            {
                return row;
            }

            return row with { Day = existing.Day };
        }).ToArray();

    private async Task<string> GetAdaptContentAsync(IChatCompletionService chat, ChatHistory history, CancellationToken ct)
    {
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.2,
            MaxTokens = 2000,
            ResponseFormat = "json_object"
        };
        for (var attempt = 1; attempt <= MaxComposeAttempts; attempt++)
        {
            try
            {
                using var cts = Deadline(ct, LlmCallBudget);
                var resp = await chat.GetChatMessageContentAsync(history, settings, _kernel, cts.Token);
                var content = resp.Content ?? "";
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }
            catch (Exception ex) when (IsTransientProviderError(ex, ct))
            {
                _logger.LogWarning(ex, "adaptation_compose_transient attempt {Attempt}/{Max}; retrying", attempt, MaxComposeAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }
        }
        return "";
    }

    private static bool TryParsePlanPatch(string? raw, out PlanPatch patch, out string error)
    {
        try
        {
            var json = ExtractJson(raw);
            patch = JsonSerializer.Deserialize<PlanPatch>(json, ContractJson.Options) ?? new PlanPatch();
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            patch = new PlanPatch();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Applies an approved plan patch to the active goal's plan (in place,
    /// preserving row order) and returns the executed-effect + updated plan slice.</summary>
    private (ExecutedEffect Effect, IReadOnlyList<PlanItem>? Updated, IReadOnlyList<string> ChangedIds, IReadOnlyList<ImpactItem> ImpactDelta) ApplyPendingPatch(string proposalId)
    {
        _approvals.MarkExecuted(proposalId);
        var active = _pendingPatches.TryRemove(proposalId, out var pending) ? _tasks.GetGoal(pending.GoalId) : null;
        if (active is null)
        {
            return (new ExecutedEffect { ProposalId = proposalId, Action = $"{PlanPatchModule}.{PlanPatchFunction}", Result = "skipped", Detail = "no pending patch or active goal" }, null, [], []);
        }

        var (newPlan, changed) = ApplyPatch(active.Plan, pending.Patch);
        active.Plan = newPlan;
        _logger.LogInformation("adaptation_applied {ProposalId} changed={Changed} plan_items={Count}", proposalId, changed.Count, newPlan.Count);
        return (
            new ExecutedEffect { ProposalId = proposalId, Action = $"{PlanPatchModule}.{PlanPatchFunction}", Result = "plan_updated", Detail = pending.Patch.Rationale },
            newPlan,
            changed,
            pending.Patch.ImpactDelta);
    }

    private static (IReadOnlyList<PlanItem> Plan, IReadOnlyList<string> Changed) ApplyPatch(IReadOnlyList<PlanItem> plan, PlanPatch patch)
    {
        var order = plan.Select(p => p.Id).ToList();
        var byId = plan.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var changed = new List<string>();

        foreach (var id in patch.Remove)
        {
            if (byId.Remove(id))
            {
                order.Remove(id);
                changed.Add(id);
            }
        }
        foreach (var row in patch.Upsert)
        {
            var next = row;
            if (!byId.ContainsKey(row.Id))
            {
                order.Add(row.Id);
                next = next.Day > 0 ? next : next with { Day = order.Count };
            }
            else if (next.Day <= 0)
            {
                next = next with { Day = byId[row.Id].Day };
            }
            byId[row.Id] = next;
            if (!changed.Contains(row.Id))
            {
                changed.Add(row.Id);
            }
        }

        var ordered = order.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        return (ordered, changed);
    }

    private static IReadOnlyList<PlanItem> AssignPlanDays(IReadOnlyList<PlanItem> plan)
        => plan.Take(7).Select((item, index) => item with { Day = index + 1 }).ToArray();

    private async Task<Status> ApplyApprovalCoreAsync(Approval approval, CancellationToken ct)
    {
        using var scope = _trace.BeginGoalScope(approval.GoalId, approval.CorrelationId);
        // Enter THIS goal's armed policy before actuating anything. This path
        // invokes approved proposals through the kernel, so the filter runs again
        // here — the last gate before a real side effect (spending money, starting
        // an appliance). It used to arm nothing at all and silently inherited
        // whatever policy the last plan run left behind.
        using var policy = _safety.EnterGoal(approval.GoalId);
        _safety.SetTrace(_trace);
        await _trace.PhaseAsync("executing");
        // The human answered, so every task waiting on that answer moves. This is
        // where a goal stops being 0% — progress is the ledger, not the clock.
        await AdvanceTasksAsync(approval.GoalId, TaskState.AwaitingApproval, TaskState.Executing);
        var cleared = _approvals.ApplyDecisions(approval);
        var executed = new List<ExecutedEffect>();
        IReadOnlyList<PlanItem>? updatedPlan = null;
        IReadOnlyList<string> changedIds = [];
        IReadOnlyList<ImpactItem> impactDelta = [];

        foreach (var proposal in cleared)
        {
            // A daily adaptation's approval APPLIES its plan patch instead of invoking
            // a kernel function — the plan itself is the effect. The updated plan ships
            // back in the status so the UI re-renders in place.
            if (proposal.Module == PlanPatchModule && proposal.Function == PlanPatchFunction)
            {
                var applied = ApplyPendingPatch(proposal.ProposalId);
                executed.Add(applied.Effect);
                if (applied.Updated is not null)
                {
                    updatedPlan = applied.Updated;
                    changedIds = applied.ChangedIds;
                    impactDelta = applied.ImpactDelta;
                }
                continue;
            }

            // PRE-CHECK GATE 2: can this effect actually happen, right now? The world
            // moves between planning and approval — and approval is precisely where
            // the delay is, because it waits on a person. An oven that was online
            // when planned can be unplugged by the time someone taps Approve.
            var effectCheck = await _prechecks.RunForProposalAsync(proposal, ct);
            if (!effectCheck.Ok)
            {
                // DEFERRED, not failed and not silently dropped: the approval still
                // stands, so re-applying it once the world recovers executes it (the
                // ledger is idempotent). MarkExecuted is deliberately NOT called —
                // marking it executed would lose the effect forever.
                _logger.LogWarning("proposal_deferred {ProposalId} {Module}.{Function}: {Why}",
                    proposal.ProposalId, proposal.Module, proposal.Function, effectCheck.Remediation);
                executed.Add(new ExecutedEffect
                {
                    ProposalId = proposal.ProposalId,
                    Action = $"{proposal.Module}.{proposal.Function}",
                    Result = ExecutionResults.DeferredPrecheck,
                    Detail = effectCheck.Remediation
                });
                continue;
            }

            var function = _kernel.Plugins.GetFunction(proposal.Module, proposal.Function);
            var args = ToKernelArguments(proposal.Args);
            _logger.LogInformation("execute_proposal {ProposalId} {Module}.{Function}", proposal.ProposalId, proposal.Module, proposal.Function);
            var invokeResult = await _kernel.InvokeAsync(function, args, ct);
            _approvals.MarkExecuted(proposal.ProposalId);
            executed.Add(new ExecutedEffect
            {
                ProposalId = proposal.ProposalId,
                Action = $"{proposal.Module}.{proposal.Function}",
                Result = "executed",
                Detail = invokeResult.ToString()
            });
        }

        // Effects are done; the plan now lives in the world and the observers watch
        // it. Monitoring is not "finished" — a plan is only complete when its last day
        // has passed — so tasks rest here, not at Completed.
        await AdvanceTasksAsync(approval.GoalId, TaskState.Executing, TaskState.Monitoring);
        var progress = _tasks.GetGoal(approval.GoalId);

        return new Status
        {
            GoalId = approval.GoalId,
            CorrelationId = approval.CorrelationId,
            // The goal is now MONITORING day by day, NOT done — reporting Done here made
            // the board jump to "completed, 100%" the instant a plan was approved. It
            // finishes only when its window/plan passes (CompleteIfWindowPassedAsync).
            TaskStatus = TaskStatuses.Monitoring,
            Payload = new StatusPayload
            {
                SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                Executed = executed,
                UpdatedPlan = updatedPlan,
                ChangedIds = changedIds,
                ImpactDelta = impactDelta,
                Note = executed.Count == 0
                    ? "No new proposals executed; approval may be a replay or rejection."
                    : $"Executed {executed.Count} proposal(s).{ProgressNote(progress)}"
            }
        };
    }

    private async Task<(Status Status, Proposal? Adaptation)> HandleControlCoreAsync(Control control, CancellationToken ct)
    {
        var correlationId = _tasks.GetGoal(control.GoalId)?.Dispatch.CorrelationId;
        using var scope = _trace.BeginGoalScope(control.GoalId, correlationId);
        // A control tick can re-plan a slice of this goal (trigger_event →
        // ProposeDailyAdaptationAsync), so enter its policy scope too.
        using var policy = _safety.EnterGoal(control.GoalId);
        await _trace.PhaseAsync("monitoring");

        if (control.Command == ControlCommands.TriggerEvent)
        {
            var eventId = control.Payload?.EventId ?? control.EventId;
            var activeGoal = _tasks.GetGoal(control.GoalId);
            if (activeGoal is null)
            {
                var noGoalStatus = BuildMonitoringStatus(
                    control.GoalId,
                    correlationId,
                    false,
                    "control trigger_event applied; no active goal is being monitored",
                    eventId);
                await _trace.ThinkingAsync(noGoalStatus.Payload.Note ?? "");
                return (noGoalStatus, null);
            }

            if (string.IsNullOrWhiteSpace(eventId))
            {
                var missingStatus = BuildMonitoringStatus(
                    control.GoalId,
                    activeGoal.Dispatch.CorrelationId,
                    false,
                    "trigger_event missing event_id",
                    eventId);
                await _trace.ThinkingAsync(missingStatus.Payload.Note ?? "");
                return (missingStatus, null);
            }

            // The goal's domain observer owns the catalog and knows how to turn one
            // entry into a change; this path just asks. (It used to read
            // daily_events out of the snapshot itself.)
            var change = _monitor.TriggerEvent(activeGoal, eventId);
            if (change is null)
            {
                var unknownStatus = BuildMonitoringStatus(
                    control.GoalId,
                    activeGoal.Dispatch.CorrelationId,
                    false,
                    $"unknown event {eventId}",
                    eventId);
                await _trace.ThinkingAsync(unknownStatus.Payload.Note ?? "");
                return (unknownStatus, null);
            }

            if (!activeGoal.EmittedMaterialChanges.Add(change.Key))
            {
                var replayStatus = BuildMonitoringStatus(
                    control.GoalId,
                    activeGoal.Dispatch.CorrelationId,
                    false,
                    "event already applied",
                    eventId);
                await _trace.ThinkingAsync(replayStatus.Payload.Note ?? "");
                return (replayStatus, null);
            }

            if (!change.Material)
            {
                var nonMaterialStatus = BuildMonitoringStatus(
                    control.GoalId,
                    activeGoal.Dispatch.CorrelationId,
                    false,
                    $"event {eventId} is not material",
                    eventId);
                await _trace.ThinkingAsync(nonMaterialStatus.Payload.Note ?? "");
                return (nonMaterialStatus, null);
            }

            await _trace.ThinkingAsync($"material event triggered: {change.Description}");
            await _trace.PhaseAsync("adapting");
            var proposal = await ProposeDailyAdaptationAsync(control.GoalId, activeGoal, change, ct, eventId);
            if (proposal is not null)
            {
                proposal = proposal with { CorrelationId = activeGoal.Dispatch.CorrelationId };
            }

            var status = BuildMonitoringStatus(
                control.GoalId,
                activeGoal.Dispatch.CorrelationId,
                true,
                $"material: {change.Description}",
                eventId);
            return (status, proposal);
        }

        if (_clock is SimulatedClock sim)
        {
            if (control.Command == ControlCommands.SetDate && control.Payload?.Date is { } date)
            {
                sim.SetDate(date);
            }
            else if (control.Command == ControlCommands.AdvanceDay)
            {
                sim.AdvanceDay();
            }
        }

        if (control.Command == ControlCommands.Reset)
        {
            var store = _kernel.Services.GetRequiredService<IProductApiAdapter>();
            await store.ResetAsync(ct);
            _tasks.RemoveGoal(control.GoalId);
            _safety.RemoveGoal(control.GoalId);
        }

        var active = _tasks.GetGoal(control.GoalId);
        if (active is null)
        {
            var noGoalStatus = new Status
            {
                GoalId = control.GoalId,
                CorrelationId = correlationId,
                TaskStatus = TaskStatuses.Monitoring,
                Payload = new StatusPayload
                {
                    Day = _clock.Today.DayOfWeek.ToString()[..3],
                    SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                    Material = false,
                    Note = $"control {control.Command} applied; no active goal is being monitored"
                }
            };
            await _trace.ThinkingAsync(noGoalStatus.Payload.Note);
            return (noGoalStatus, null);
        }

        // A monitored goal has to be able to FINISH, or the board shows it working
        // forever. It is done when the world moves past what it planned for: the
        // time window closes. Derived from the clock against the contract — the
        // agent doesn't decide it's finished, the calendar does.
        if (await CompleteIfWindowPassedAsync(active, ct))
        {
            var doneStatus = BuildMonitoringStatus(
                control.GoalId,
                active.Dispatch.CorrelationId,
                false,
                $"goal complete — its time window closed on {active.Dispatch.TimeWindow.End}.{ProgressNote(active)}",
                null);
            await _trace.ThinkingAsync(doneStatus.Payload.Note ?? "");
            return (doneStatus with { TaskStatus = TaskStatuses.Done }, null);
        }

        var changes = await _monitor.ObserveAsync(active, ct);
        var material = changes.FirstOrDefault(c => c.Material && active.EmittedMaterialChanges.Add(c.Key));
        if (material is not null)
        {
            await _trace.ThinkingAsync($"material change detected: {material.Description}");
            await _trace.PhaseAsync("adapting");
            // Daily-feed changes carry a steer → a SCOPED LLM re-plan produces a plan
            // patch. Other changes (guest RSVP) keep the deterministic effect proposal.
            var proposal = material.Steer is not null
                ? await ProposeDailyAdaptationAsync(control.GoalId, active, material, ct)
                : await _monitor.ProposeAdaptationAsync(control.GoalId, material, ct);
            if (proposal is not null)
            {
                proposal = proposal with { CorrelationId = active.Dispatch.CorrelationId };
            }

            var status = BuildMonitoringStatus(control.GoalId, active.Dispatch.CorrelationId, true, $"material: {material.Description}");
            return (status, proposal);
        }

        var quietNote = changes.Any(c => c.Material)
            ? "on track; material change already surfaced for approval."
            : "on track; no material world changes affect the active plan.";
        await _trace.ThinkingAsync(quietNote);
        var quietStatus = new Status
        {
            GoalId = control.GoalId,
            CorrelationId = active.Dispatch.CorrelationId,
            TaskStatus = TaskStatuses.Monitoring,
            Payload = new StatusPayload
            {
                Day = _clock.Today.DayOfWeek.ToString()[..3],
                SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                Material = false,
                Note = quietNote
            }
        };
        return (quietStatus, null);
    }

    private Status BuildMonitoringStatus(string goalId, string? correlationId, bool material, string note, string? eventId = null)
        => new()
        {
            GoalId = goalId,
            CorrelationId = correlationId,
            TaskStatus = TaskStatuses.Monitoring,
            Payload = new StatusPayload
            {
                Day = _clock.Today.DayOfWeek.ToString()[..3],
                SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                EventId = eventId,
                Material = material,
                Note = note
            }
        };

    /// <summary>
    /// The planner's grounding tool set — DERIVED by the Capability Manager from
    /// what the product pack registered, not hand-listed here. Both the CONTENT
    /// and the ORDER are what the LLM receives as its tools array; the M0 gate
    /// (<c>--dump-capabilities</c>) diffs both against what the old hand-written
    /// whitelist produced.
    /// </summary>
    internal IReadOnlyList<KernelFunction> GroundingFunctions()
        => _capabilities.GetGroundingFunctions(_kernel);

    private ProposalItem NormalizeProposal(ProposalItem proposal)
    {
        var tier = _capabilities.GetSideEffectTier(proposal.Module, proposal.Function) ?? proposal.Tier;
        return proposal with
        {
            Tier = tier,
            RequiresApproval = true,
            ProposalId = string.IsNullOrWhiteSpace(proposal.ProposalId) ? $"p{_approvals.All().Count + 1}" : proposal.ProposalId
        };
    }

    private static KernelArguments ToKernelArguments(JsonObject? args)
    {
        var result = new KernelArguments();
        if (args is null)
        {
            return result;
        }

        foreach (var (key, value) in args)
        {
            var normalized = key == "estimated_total" ? "estimatedTotal" : key;
            result[normalized] = value switch
            {
                JsonArray arr => arr.Select(ArrayValueToString).Where(v => v.Length > 0).ToArray(),
                JsonValue val when val.TryGetValue<double>(out var d) => d,
                JsonValue val when val.TryGetValue<int>(out var i) => i,
                JsonValue val when val.TryGetValue<string>(out var s) => s,
                JsonValue val when val.TryGetValue<bool>(out var b) => b,
                _ => value?.ToJsonString(ContractJson.Options)
            };
        }

        return result;
    }

    private static string ArrayValueToString(JsonNode? value)
    {
        if (value is null)
        {
            return "";
        }

        if (value is JsonObject obj && obj["name"] is not null)
        {
            return obj["name"]!.GetValue<string>();
        }

        return value.GetValue<string>();
    }

    private static ModelPlan ParseModelPlan(string? raw)
    {
        var json = ExtractJson(raw);
        return JsonSerializer.Deserialize<ModelPlan>(json, ContractJson.Options)
            ?? throw new JsonException("Planner returned null JSON.");
    }

    private static bool TryParseModelPlan(string? raw, out ModelPlan modelPlan, out string error)
    {
        try
        {
            modelPlan = ParseModelPlan(raw);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            modelPlan = new ModelPlan();
            error = ex.Message;
            return false;
        }
    }

    private static string ExtractJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new JsonException("Planner did not return any content for the JSON plan.");
        }

        var trimmed = StripCodeFence(raw.Trim());
        var start = trimmed.IndexOf('{');
        if (start < 0)
        {
            throw new JsonException($"Planner did not return a JSON object. Raw: {raw}");
        }

        var end = FindMatchingObjectEnd(trimmed, start);
        if (end < 0)
        {
            throw new JsonException($"Planner returned an incomplete JSON object. Raw: {raw}");
        }

        return trimmed[start..(end + 1)];
    }

    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline
            ? trimmed[(firstNewline + 1)..lastFence].Trim()
            : trimmed;
    }

    private static int FindMatchingObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaping = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaping)
                {
                    escaping = false;
                }
                else if (ch == '\\')
                {
                    escaping = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// A deadline for ONE provider call, linked to the goal's own token.
    ///
    /// <para>
    /// Without this a goal can hang forever. Observed twice in one session: the stream
    /// delivered tokens, stopped mid-JSON, and never returned — the process stayed
    /// alive, nothing was logged, and every surface kept reporting "Working out the
    /// steps…". The provider was healthy; OpenRouter had simply routed that stream to
    /// one that hung.
    /// </para>
    /// <para>
    /// <c>HttpClient.Timeout</c> does NOT cover this. Streaming reads the response with
    /// <c>ResponseHeadersRead</c>, so the timeout is satisfied the moment headers
    /// arrive — everything after that is an unbounded read. The deadline has to be a
    /// cancellation token.
    /// </para>
    /// <para>
    /// Expiry cancels only the LINKED token, never the caller's, so
    /// <see cref="IsTransientProviderError"/> sees <c>ct.IsCancellationRequested ==
    /// false</c> and classifies it as transient. That is deliberate: a hang then flows
    /// into the retry machinery that already exists for provider flakiness, rather than
    /// needing a second path. A genuine shutdown cancels <c>ct</c> itself and still
    /// propagates.
    /// </para>
    /// </summary>
    private static CancellationTokenSource Deadline(CancellationToken ct, TimeSpan budget)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        return cts;
    }

    /// <summary>
    /// True for TRANSPORT/provider flakiness worth retrying the LLM over — a
    /// finish_reason the OpenAI SDK can't deserialize (throws
    /// <see cref="ArgumentOutOfRangeException"/>), a JSON deserialization glitch in
    /// the SDK, an HTTP 5xx/429/timeout, or our own per-call deadline expiring — as
    /// opposed to a genuine cancellation (which must propagate) or a modelling error
    /// (handled by the parse retry).
    /// </summary>
    /// <summary>
    /// Gate 15's window onto the classifier. The gate's whole point is that a fired
    /// deadline is judged TRANSIENT while a real cancellation is not — asserting that
    /// against a copy of the rule would test the copy.
    /// </summary>
    internal static bool IsTransientProviderErrorForTests(Exception ex, CancellationToken ct)
        => IsTransientProviderError(ex, ct);

    private static bool IsTransientProviderError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return false; // genuine cancellation — never swallow it
        }

        var text = ex.ToString();
        return ex is HttpRequestException
            || ex is OperationCanceledException     // our own per-call deadline fired (ct excluded above)
            || ex is TaskCanceledException          // client-side/socket timeout (ct excluded above)
            || ex is JsonException                  // SDK failed to deserialize the provider response
            || ex is ArgumentOutOfRangeException     // e.g. "Unknown ChatFinishReason value"
            || text.Contains("ChatFinishReason", StringComparison.OrdinalIgnoreCase)
            || text.Contains("finish_reason", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
            || text.Contains(" 502", StringComparison.Ordinal)
            || text.Contains(" 503", StringComparison.Ordinal)
            || text.Contains(" 429", StringComparison.Ordinal);
    }

    private static bool LooksLikeResponseFormatRejection(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || message.Contains("json_object", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ResponseFormat", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ModelPlan
    {
        public IReadOnlyList<PlanItem> Plan { get; init; } = [];
        public IReadOnlyList<ProposalItem> Proposals { get; init; } = [];
        public IReadOnlyList<ImpactItem> Impact { get; init; } = [];
        public string? Explanation { get; init; }
    }
}
