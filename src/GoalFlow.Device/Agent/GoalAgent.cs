using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Capabilities;
using GoalFlow.Device.Modules.Steering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

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
        // TODO(M1):
        //   var ground = await _grounding.AssembleAsync(dispatch, _kernel, ct);
        //   _safety.SetPolicy(dispatch.Constraints.Hard);
        //   var history = new ChatHistory(_grounding.RenderPrompt(ground));
        //   history.AddUserMessage(BuildPlanningInstruction(dispatch));
        //   var settings = new OpenAIPromptExecutionSettings
        //       { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
        //   var chat = _kernel.GetRequiredService<IChatCompletionService>();
        //   await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, _kernel, ct))
        //       -> _trace.ThinkingAsync(...) / tool events via the filter+Trace hooks;
        //   parse the model's final structured plan -> PlanItems; Register proposals; build PlanReady.
        throw new NotImplementedException("v2-M0 design skeleton");
    }

    /// <summary>
    /// Applies an approval frame: ApprovalCoordinator flips decisions, then the
    /// Actuator half executes each cleared proposal by invoking its frozen
    /// {module}.{function}(args) through the kernel (filter still applies),
    /// idempotently (MarkExecuted). Returns a status frame.
    /// </summary>
    public Task<Status> ApplyApprovalAsync(Approval approval, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>
    /// Handles a control frame against the GENERIC clock: set_date/advance_day
    /// drive the SimulatedClock, reset restores the mock world; afterwards
    /// MonitorAdapt observes the (re-anchored) world and may yield an
    /// adaptation <see cref="Proposal"/> alongside the status.
    /// </summary>
    public Task<(Status Status, Proposal? Adaptation)> HandleControlAsync(Control control, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>The planning instruction (user message) rendered from the contract.</summary>
    internal static string BuildPlanningInstruction(Dispatch dispatch)
        => throw new NotImplementedException("v2-M0 design skeleton");
}
