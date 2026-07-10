using GoalFlow.Device.Agent;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Capabilities;
using GoalFlow.Device.Modules.Steering;
using GoalFlow.Device.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// GoalFlow device agent — v2 command-line entry (v2-M0 DESIGN SKELETON).
//
// Usage:
//   dotnet run -- --goal "help us eat healthier this week" [--domain meal_plan]
//   dotnet run -- --contract data/sample-contract.json
//   dotnet run -- --connect ws://localhost:8787/ws
//   ... plus:  [--data ./data] [--date 2026-07-14]
//
// GENERIC CLOCK: with no --date the agent runs on the REAL system clock
// (SystemClock). --date <ISO> (or a control set_date frame) starts a
// SimulatedClock there; control advance_day steps it. There is NO hardcoded
// anchor date anywhere — mock data stores day offsets resolved against the
// clock at read time.
//
// LLM-ONLY: planning always goes through the SK kernel + OpenRouter. No
// rules/scripted planner exists in v2.

var options = CliOptions.Parse(args);
DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var services = new ServiceCollection();

// Structured logging: console, leveled; goal/correlation ids attach via Trace scopes.
services.AddLogging(logging => logging
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    })
    .SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information));

// Scheduler/Clock: real today by default; simulated only when asked for.
services.AddSingleton<IClock>(_ => options.Date is { } start
    ? new SimulatedClock(DateOnly.Parse(start))
    : new SystemClock());

// Mock world + capability plugins (meal domain + shared).
services.AddSingleton(sp => new MockWorldStore(options.DataDir, sp.GetRequiredService<IClock>()));
services.AddSingleton<InventoryPlugin>();
services.AddSingleton<CalendarPlugin>();
services.AddSingleton<RecipePlugin>();
services.AddSingleton<ShoppingListPlugin>();
services.AddSingleton<ReminderPlugin>();
services.AddSingleton<ApplianceControlPlugin>();
services.AddSingleton<FamilyProfilesPlugin>();
services.AddSingleton<BudgetPlugin>();
services.AddSingleton<NotifyPlugin>();

// Steering modules.
services.AddSingleton<SafetyFilter>();
services.AddSingleton<ApprovalCoordinator>();
services.AddSingleton<Grounding>();
services.AddSingleton<MaterialityPolicy>();
services.AddSingleton<MonitorAdapt>();
services.AddSingleton<CapabilityRegistry>();

await using var provider = services.BuildServiceProvider();

// TODO(M1): wire the composition root end-to-end:
//   var settings = new AgentSettings
//   {
//       ApiKey  = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? throw ...,
//       BaseUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ?? default,
//       ModelId = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "openai/gpt-oss-120b",
//   };
//   var kernel = GoalAgent.BuildKernel(settings, provider);
//   Func<AgentEvent, Task> emit = ...;      // WsClient.SendAsync in --connect mode,
//                                           // stderr JSON lines in offline modes
//   var trace = new Trace(loggerFactory.CreateLogger<Trace>(), emit);
//   var agent = new GoalAgent(kernel, trace, grounding, safety, approvals, monitor, clock, logger);
//
//   if (options.ConnectUrl is { } url)      // --connect: full duplex loop
//       -> WsClient.ConnectAsync(CapabilityRegistry.BuildCapabilitiesMessage(kernel))
//          + RunReceiveLoopAsync routing dispatch/approval/control -> agent.
//   else if (options.ContractPath is { } path)   // --contract: one-shot file mode
//       -> deserialize Dispatch (resolving ${today+N} tokens against the clock),
//          agent.RunAsync, print plan_ready JSON to stdout (events to stderr).
//   else if (options.Goal is { } goal)      // --goal: local dispatch synthesized
//       -> build a minimal Dispatch { Domain = options.Domain, Objective = goal, ... }
//          over the clock's real dates, then run as above.
throw new NotImplementedException("v2-M0 design skeleton: composition root wiring lands in M1.");

/// <summary>Parsed command-line options for the v2 entry point.</summary>
internal sealed record CliOptions
{
    /// <summary>--goal "..." — natural-language goal, dispatched locally.</summary>
    public string? Goal { get; init; }

    /// <summary>--domain — use-case name for --goal mode (default meal_plan).</summary>
    public string Domain { get; init; } = "meal_plan";

    /// <summary>--contract &lt;file&gt; — run a dispatch frame from disk.</summary>
    public string? ContractPath { get; init; }

    /// <summary>--connect &lt;ws url&gt; — live cloud session.</summary>
    public string? ConnectUrl { get; init; }

    /// <summary>--date &lt;ISO&gt; — start a SimulatedClock here. Null = real today (SystemClock).</summary>
    public string? Date { get; init; }

    /// <summary>--data &lt;dir&gt; — mock world directory (default ./data).</summary>
    public string DataDir { get; init; } = "data";

    /// <summary>--verbose — debug-level logging.</summary>
    public bool Verbose { get; init; }

    public static CliOptions Parse(string[] args)
        => throw new NotImplementedException("v2-M0 design skeleton");
}

/// <summary>Minimal KEY=VALUE .env loader (BCL only; missing file is fine).</summary>
internal static class DotEnv
{
    public static void Load(string path)
        => throw new NotImplementedException("v2-M0 design skeleton");
}
