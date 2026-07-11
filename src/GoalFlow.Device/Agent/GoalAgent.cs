using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Capabilities;
using GoalFlow.Device.Modules.Steering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    private readonly IClock _clock;
    private readonly ILogger<GoalAgent> _logger;
    private readonly Dictionary<string, ActiveGoalContext> _activeGoals = new(StringComparer.Ordinal);

    /// <summary>Plan patches awaiting approval, keyed by proposal id → (goalId, patch).
    /// Registered when a daily adaptation is proposed; applied in ApplyApproval.</summary>
    private readonly Dictionary<string, (string GoalId, PlanPatch Patch)> _pendingPatches = new(StringComparer.Ordinal);

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
        IClock clock,
        ILogger<GoalAgent> logger)
    {
        _kernel = kernel;
        _trace = trace;
        _grounding = grounding;
        _safety = safety;
        _approvals = approvals;
        _monitor = monitor;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Builds the device kernel:
    ///   1. OpenRouter chat completion (OpenAI-compatible connector; model
    ///      <see cref="AgentSettings.ModelId"/>, endpoint <see cref="AgentSettings.BaseUrl"/>).
    ///   2. Capability plugins from DI, each under its advertised module name:
    ///      Inventory, Calendar, Recipes, ShoppingList, Reminders, Guests, Appliance,
    ///      FamilyProfiles, Budget, Notify.
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
        builder.Services.AddSingleton(services.GetRequiredService<MockWorldStore>());

        builder.Plugins.AddFromObject(services.GetRequiredService<InventoryPlugin>(), "Inventory");
        builder.Plugins.AddFromObject(services.GetRequiredService<CalendarPlugin>(), "Calendar");
        builder.Plugins.AddFromObject(services.GetRequiredService<RecipePlugin>(), "Recipes");
        builder.Plugins.AddFromObject(services.GetRequiredService<ShoppingListPlugin>(), "ShoppingList");
        builder.Plugins.AddFromObject(services.GetRequiredService<ReminderPlugin>(), "Reminders");
        builder.Plugins.AddFromObject(services.GetRequiredService<GuestsPlugin>(), "Guests");
        builder.Plugins.AddFromObject(services.GetRequiredService<ApplianceControlPlugin>(), "Appliance");
        builder.Plugins.AddFromObject(services.GetRequiredService<FamilyProfilesPlugin>(), "FamilyProfiles");
        builder.Plugins.AddFromObject(services.GetRequiredService<BudgetPlugin>(), "Budget");
        builder.Plugins.AddFromObject(services.GetRequiredService<NotifyPlugin>(), "Notify");

        return builder.Build();
    }

    /// <summary>
    /// Runs one dispatch to a plan, STREAMING agent_events throughout:
    ///   phase(grounding)  → Grounding.AssembleAsync; SafetyFilter.SetPolicy(constraints.hard)
    ///   phase(planning)   → compose the final JSON plan without tools;
    ///                       thinking/tool_call/tool_result/plan_progress events still flow
    ///   phase(checking)   → collect SafetyFilter verdict + freeze side-effects into
    ///                       tiered proposals via ApprovalCoordinator
    ///   phase(awaiting_approval) → return the plan_ready frame.
    /// </summary>
    public Task<PlanReady> RunAsync(Dispatch dispatch, CancellationToken ct = default)
    {
        return RunCoreAsync(dispatch, ct);
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

    private const int MaxComposeAttempts = 3;

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
        - For meal_plan goals, produce exactly ONE dinner plan item per DAY for EVERY date in the contract time_window, inclusive of both start and end dates, with no gaps and no extra days. Set each plan item's "when" to that date in YYYY-MM-DD format, so a 7-day window yields 7 dinner items and a 5-day window yields 5.
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
        _safety.SetPolicy(dispatch.Constraints.Hard);
        _safety.SetTrace(_trace);

        _logger.LogInformation("plan_start domain={Domain} model_clock={Today}", dispatch.Domain, _clock.Today);
        await _trace.PhaseAsync("grounding");
        var ground = await _grounding.AssembleAsync(dispatch, _kernel, ct);

        var history = new ChatHistory();
        history.AddSystemMessage(_grounding.RenderPrompt(ground));
        history.AddUserMessage(BuildGroundingInstruction(dispatch));

        var groundingSettings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(ReadOnlyPlanningFunctions(), autoInvoke: true, options: new FunctionChoiceBehaviorOptions
            {
                AllowConcurrentInvocation = false,
                AllowParallelCalls = false
            }),
            Temperature = 0.2,
            MaxTokens = 2500
        };

        var chat = _kernel.Services.GetRequiredService<IChatCompletionService>();
        var groundingSummary = new StringBuilder();
        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, groundingSettings, _kernel, ct))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                groundingSummary.Append(chunk.Content);
                await _trace.ThinkingAsync(chunk.Content);
            }
        }

        if (groundingSummary.Length > 0)
        {
            history.AddAssistantMessage(groundingSummary.ToString());
        }

        await _trace.PhaseAsync("planning");
        var modelPlan = await ComposeModelPlanAsync(chat, history, dispatch, ct);

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

        var ready = new PlanReady
        {
            GoalId = dispatch.GoalId,
            CorrelationId = dispatch.CorrelationId,
            TaskStatus = TaskStatuses.AwaitingApproval,
            Payload = new PlanReadyPayload
            {
                Plan = modelPlan.Plan,
                Proposals = proposals,
                Safety = new SafetyVerdict { Gate = _safety.Gate, Violations = _safety.Violations.ToArray() },
                Impact = modelPlan.Impact,
                Explanation = modelPlan.Explanation
            }
        };

        await _trace.PhaseAsync("awaiting_approval");
        _activeGoals[dispatch.GoalId] = new ActiveGoalContext
        {
            Dispatch = dispatch,
            Plan = modelPlan.Plan,
            WorldSnapshot = await _monitor.CaptureSnapshotAsync(ct)
        };
        _logger.LogInformation("plan_ready items={ItemCount} proposals={ProposalCount} safety={Safety}", ready.Payload.Plan.Count, ready.Payload.Proposals.Count, ready.Payload.Safety.Gate);
        return ready;
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
            var response = await chat.GetChatMessageContentAsync(history, jsonSettings, _kernel, ct);
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
        var response = await chat.GetChatMessageContentAsync(history, strictSettings, _kernel, ct);
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
          "upsert": [ { "id": "<existing id to REPLACE, or a new id to ADD>", "title": "...", "detail": "...", "when": "YYYY-MM-DD", "why": ["short reason"] } ],
          "remove": ["<id to drop>"],
          "impact_delta": [ { "label": "waste", "value": "-2 items" } ],
          "rationale": "one sentence explaining the change"
        }
        Keep it tiny: usually a single upsert. Reuse an existing id to SWAP that row;
        use a new id only to ADD a step. Honor the steer.
        """;

    private async Task<Proposal?> ProposeDailyAdaptationAsync(string goalId, ActiveGoalContext active, WorldChange change, CancellationToken ct)
    {
        var chat = _kernel.Services.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(AdaptSystemPrompt);
        history.AddUserMessage(BuildAdaptInstruction(active.Plan, change));

        var raw = await GetAdaptContentAsync(chat, history, ct);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            await _trace.ThinkingAsync(raw);
        }

        if (!TryParsePlanPatch(raw, out var patch, out var error) || (patch.Upsert.Count == 0 && patch.Remove.Count == 0))
        {
            _logger.LogWarning("adaptation_patch_unusable kind={Kind}: {Error}", change.Kind, error);
            await _trace.ThinkingAsync($"planner_notice: adaptation produced no usable patch ({error}); leaving plan unchanged.");
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
                Tier = ApprovalTiers.Adapt,
                RequiresApproval = true,
                Patch = patch
            }
        };
    }

    private static string BuildAdaptInstruction(IReadOnlyList<PlanItem> plan, WorldChange change)
    {
        var planLines = string.Join("\n", plan.Select(p =>
            $"- {p.Id} | {p.When ?? "n/a"} | {p.Title}{(p.Detail is null ? "" : " — " + p.Detail)}"));
        var context = change.Context is null ? "" : $"\nDetails: {change.Context.ToJsonString()}";
        return $"""
            CURRENT PLAN (id | date | title):
            {planLines}

            WORLD CHANGE: {change.Description}{context}
            HOW TO ADAPT: {change.Steer}

            Return the minimal JSON patch for the affected row(s) only.
            """;
    }

    private async Task<string> GetAdaptContentAsync(IChatCompletionService chat, ChatHistory history, CancellationToken ct)
    {
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.2,
            MaxTokens = 900,
            ResponseFormat = "json_object"
        };
        for (var attempt = 1; attempt <= MaxComposeAttempts; attempt++)
        {
            try
            {
                var resp = await chat.GetChatMessageContentAsync(history, settings, _kernel, ct);
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
        if (!_pendingPatches.Remove(proposalId, out var pending) || !_activeGoals.TryGetValue(pending.GoalId, out var active))
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
            if (!byId.ContainsKey(row.Id))
            {
                order.Add(row.Id);
            }
            byId[row.Id] = row;
            if (!changed.Contains(row.Id))
            {
                changed.Add(row.Id);
            }
        }

        var ordered = order.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        return (ordered, changed);
    }

    private async Task<Status> ApplyApprovalCoreAsync(Approval approval, CancellationToken ct)
    {
        using var scope = _trace.BeginGoalScope(approval.GoalId, approval.CorrelationId);
        _safety.SetTrace(_trace);
        await _trace.PhaseAsync("executing");
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

        return new Status
        {
            GoalId = approval.GoalId,
            CorrelationId = approval.CorrelationId,
            TaskStatus = TaskStatuses.Done,
            Payload = new StatusPayload
            {
                SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                Executed = executed,
                UpdatedPlan = updatedPlan,
                ChangedIds = changedIds,
                ImpactDelta = impactDelta,
                Note = executed.Count == 0 ? "No new proposals executed; approval may be a replay or rejection." : $"Executed {executed.Count} proposal(s)."
            }
        };
    }

    private async Task<(Status Status, Proposal? Adaptation)> HandleControlCoreAsync(Control control, CancellationToken ct)
    {
        var correlationId = _activeGoals.TryGetValue(control.GoalId, out var activeForScope)
            ? activeForScope.Dispatch.CorrelationId
            : null;
        using var scope = _trace.BeginGoalScope(control.GoalId, correlationId);
        await _trace.PhaseAsync("monitoring");

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
            var store = _kernel.Services.GetRequiredService<MockWorldStore>();
            await store.ResetAsync(ct);
            _activeGoals.Remove(control.GoalId);
        }

        if (!_activeGoals.TryGetValue(control.GoalId, out var active))
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

    private Status BuildMonitoringStatus(string goalId, string? correlationId, bool material, string note)
        => new()
        {
            GoalId = goalId,
            CorrelationId = correlationId,
            TaskStatus = TaskStatuses.Monitoring,
            Payload = new StatusPayload
            {
                Day = _clock.Today.DayOfWeek.ToString()[..3],
                SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                Material = material,
                Note = note
            }
        };

    private IReadOnlyList<KernelFunction> ReadOnlyPlanningFunctions()
    {
        var names = new (string Module, string Function)[]
        {
            ("Inventory", "ListItems"),
            ("Inventory", "GetExpiringItems"),
            ("Inventory", "CheckAvailability"),
            ("Calendar", "GetEvents"),
            ("Calendar", "GetBusyEvenings"),
            ("Recipes", "FindRecipes"),
            ("Recipes", "GetRecipe"),
            ("ShoppingList", "GetList"),
            ("Reminders", "List"),
            ("Guests", "GetEvent"),
            ("Guests", "GetGuests"),
            ("Guests", "GetDietaryConstraints"),
            ("Appliance", "ListAppliances")
        };
        return names.Select(n => _kernel.Plugins.GetFunction(n.Module, n.Function)).ToArray();
    }

    private ProposalItem NormalizeProposal(ProposalItem proposal)
    {
        var tier = CapabilityRegistry.GetSideEffectTier(proposal.Module, proposal.Function) ?? proposal.Tier;
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
    /// True for TRANSPORT/provider flakiness worth retrying the LLM over — a
    /// finish_reason the OpenAI SDK can't deserialize (throws
    /// <see cref="ArgumentOutOfRangeException"/>), a JSON deserialization glitch in
    /// the SDK, an HTTP 5xx/429/timeout — as opposed to a genuine cancellation
    /// (which must propagate) or a modelling error (handled by the parse retry).
    /// </summary>
    private static bool IsTransientProviderError(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return false; // genuine cancellation — never swallow it
        }

        var text = ex.ToString();
        return ex is HttpRequestException
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
