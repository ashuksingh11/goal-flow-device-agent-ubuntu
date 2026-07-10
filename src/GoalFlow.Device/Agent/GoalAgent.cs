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
/// with <c>FunctionChoiceBehavior.Auto</c>: the LLM decides which
/// [KernelFunction]s to call, the kernel invokes them (through the filter),
/// and this class narrates everything as <c>agent_event</c> frames via Trace.
/// LLM-ONLY: there is no rules/scripted fallback.
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
    ///      Inventory, Calendar, Recipes, ShoppingList, Reminders, Appliance,
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
        builder.Plugins.AddFromObject(services.GetRequiredService<ApplianceControlPlugin>(), "Appliance");
        builder.Plugins.AddFromObject(services.GetRequiredService<FamilyProfilesPlugin>(), "FamilyProfiles");
        builder.Plugins.AddFromObject(services.GetRequiredService<BudgetPlugin>(), "Budget");
        builder.Plugins.AddFromObject(services.GetRequiredService<NotifyPlugin>(), "Notify");

        return builder.Build();
    }

    /// <summary>
    /// Runs one dispatch to a plan, STREAMING agent_events throughout:
    ///   phase(grounding)  → Grounding.AssembleAsync; SafetyFilter.SetPolicy(constraints.hard)
    ///   phase(planning)   → streaming chat completion with FunctionChoiceBehavior.Auto;
    ///                       thinking/tool_call/tool_result/plan_progress events as tokens
    ///                       and function invocations flow
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

    /// <summary>The planning instruction (user message) rendered from the contract.</summary>
    internal static string BuildPlanningInstruction(Dispatch dispatch)
        => $$"""
        Task Contract:
        {{ContractJson.Serialize(dispatch)}}

        Planning rules:
        - This is LLM-only planning. Use Semantic Kernel read-only tools for grounding; do not invent inventory, calendar, recipe, reminder, or shopping-list facts.
        - Before final JSON, call these read tools when relevant: Inventory.ListItems, Inventory.GetExpiringItems, Calendar.GetBusyEvenings, Recipes.FindRecipes, ShoppingList.GetList.
        - During planning side effects are intentionally not exposed as tools. Propose mutations in the final JSON instead.
        - Proposal module/function/args must match real side-effecting functions exactly.
        - Do not propose ingredients or recipes that violate hard constraints.
        - Use ISO dates inside the contract time_window. Never use a hardcoded anchor date.

        Final answer must be only valid JSON with this shape:
        {
          "plan": [
            {"id":"s1","title":"...","detail":"...","when":"YYYY-MM-DD","why":["..."],"tags":["..."]}
          ],
          "proposals": [
            {"proposal_id":"p1","action":"add missing groceries","module":"ShoppingList","function":"Add","args":{"items":["..."],"reason":"..."},"tier":"light","reason":"...","requires_approval":true},
            {"proposal_id":"p2","action":"place grocery order","module":"ShoppingList","function":"PlaceOrder","args":{"estimatedTotal":42.50},"tier":"firm","reason":"...","requires_approval":true}
          ],
          "impact": [{"label":"waste","value":"uses 2 expiring items"}],
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

        await _trace.PhaseAsync("planning");
        var history = new ChatHistory();
        history.AddSystemMessage(_grounding.RenderPrompt(ground));
        history.AddUserMessage(BuildPlanningInstruction(dispatch));

        var settings = new OpenAIPromptExecutionSettings
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
        var final = new StringBuilder();
        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, _kernel, ct))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                final.Append(chunk.Content);
                await _trace.ThinkingAsync(chunk.Content);
            }
        }

        await _trace.PhaseAsync("checking");
        var modelPlan = ParseModelPlan(final.ToString());
        var proposals = modelPlan.Proposals.Select(NormalizeProposal).Select(_approvals.Register).ToArray();
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
        _logger.LogInformation("plan_ready items={ItemCount} proposals={ProposalCount} safety={Safety}", ready.Payload.Plan.Count, ready.Payload.Proposals.Count, ready.Payload.Safety.Gate);
        return ready;
    }

    private async Task<Status> ApplyApprovalCoreAsync(Approval approval, CancellationToken ct)
    {
        using var scope = _trace.BeginGoalScope(approval.GoalId, approval.CorrelationId);
        _safety.SetTrace(_trace);
        await _trace.PhaseAsync("executing");
        var cleared = _approvals.ApplyDecisions(approval);
        var executed = new List<string>();

        foreach (var proposal in cleared)
        {
            var function = _kernel.Plugins.GetFunction(proposal.Module, proposal.Function);
            var args = ToKernelArguments(proposal.Args);
            _logger.LogInformation("execute_proposal {ProposalId} {Module}.{Function}", proposal.ProposalId, proposal.Module, proposal.Function);
            await _kernel.InvokeAsync(function, args, ct);
            _approvals.MarkExecuted(proposal.ProposalId);
            executed.Add(proposal.ProposalId);
        }

        var allExecuted = _approvals.ExecutedIds();
        return new Status
        {
            GoalId = approval.GoalId,
            CorrelationId = approval.CorrelationId,
            TaskStatus = TaskStatuses.Done,
            Payload = new StatusPayload
            {
                SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                Executed = allExecuted,
                Note = executed.Count == 0 ? "No new proposals executed; approval may be a replay or rejection." : $"Executed {executed.Count} proposal(s)."
            }
        };
    }

    private async Task<(Status Status, Proposal? Adaptation)> HandleControlCoreAsync(Control control, CancellationToken ct)
    {
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
        }

        var status = new Status
        {
            GoalId = control.GoalId,
            TaskStatus = TaskStatuses.Monitoring,
            Payload = new StatusPayload
            {
                SimDate = _clock.Today.ToString("yyyy-MM-dd"),
                Material = false,
                Note = $"control {control.Command} applied"
            }
        };
        return (status, null);
    }

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
            ("Reminders", "List")
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

    private static ModelPlan ParseModelPlan(string raw)
    {
        var json = ExtractJson(raw);
        return JsonSerializer.Deserialize<ModelPlan>(json, ContractJson.Options)
            ?? throw new JsonException("Planner returned null JSON.");
    }

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new JsonException($"Planner did not return a JSON object. Raw: {raw}");
        }

        return trimmed[start..(end + 1)];
    }

    private sealed record ModelPlan
    {
        public IReadOnlyList<PlanItem> Plan { get; init; } = [];
        public IReadOnlyList<ProposalItem> Proposals { get; init; } = [];
        public IReadOnlyList<ImpactItem> Impact { get; init; } = [];
        public string? Explanation { get; init; }
    }
}
