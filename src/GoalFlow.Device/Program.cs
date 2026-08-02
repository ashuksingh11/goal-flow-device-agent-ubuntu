using GoalFlow.Device.Agent;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using GoalFlow.Device.Products.FamilyHub;
using GoalFlow.Device.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
// A per-instance --data dir (running two agents side by side) is seeded from
// ./data on first use, so it never dies on a missing calendar.json.
ProgramHelpers.EnsureDataDir(options.DataDir);
var tempDataDir = options.SimulateWeek || options.SimulateGuest ? ProgramHelpers.CopyDataToTemp(options.DataDir) : null;
if (tempDataDir is not null)
{
    options = options with { DataDir = tempDataDir };
}

var services = new ServiceCollection();

// Structured logging: console, leveled; goal/correlation ids attach via Trace scopes.
services.AddLogging(logging => logging
    .ClearProviders()
    .AddProvider(new ProgramHelpers.StderrLoggerProvider())
    .SetMinimumLevel(ProgramHelpers.ParseLogLevel() ?? (options.Verbose ? LogLevel.Debug : LogLevel.Information)));

// Scheduler/Clock: ALWAYS a SimulatedClock anchored at real today (or --date),
// so the demo's Advance day / Set date controls work — a SystemClock can't be
// advanced, which silently broke advance_day in live --connect mode. It still
// starts at today, so plan dates stay relative to today.
services.AddSingleton<IClock>(_ => options.Date is { } start
    ? new SimulatedClock(DateOnly.Parse(start))
    : new SimulatedClock());

// THE PRODUCT PACK: the mock world (behind IProductApiAdapter), the capability
// plugins, and the CapabilityManager over them. This is the ONLY line here that
// knows what product this is — swapping packs is the whole extension story.
services.AddFamilyHub(options.DataDir);

// Harness components (generic — no product types).
// ArmedPolicies is registered BEFORE the filter and depends on nothing: the filter
// enforces the armed policy, capability plugins only read it (v6). Injecting the
// filter itself into a plugin would close a cycle through CapabilityManager and
// deadlock the container at startup — see ArmedPolicies.
services.AddSingleton<ArmedPolicies>();
services.AddSingleton<IActivePolicy>(sp => sp.GetRequiredService<ArmedPolicies>());
services.AddSingleton<SafetyFilter>();
services.AddSingleton<ApprovalCoordinator>();
services.AddSingleton<Grounding>();
services.AddSingleton<RepeatReadFilter>();
services.AddSingleton<ToolRoundFilter>();
services.AddSingleton<MonitorAdapt>();
services.AddSingleton<PrecheckEngine>();

await using var provider = services.BuildServiceProvider();

var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
var settings = new AgentSettings
{
    ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
        ?? throw new InvalidOperationException("OPENROUTER_API_KEY is required."),
    BaseUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ?? "https://openrouter.ai/api/v1",
    ModelId = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "openai/gpt-oss-120b",
};
// Per-call LLM deadlines are tunable (slow/large models push a legitimate compose past
// the default) — override only when the env var is a positive int, else keep the default.
if (ProgramHelpers.TryParsePositiveInt(Environment.GetEnvironmentVariable("LLM_CALL_TIMEOUT_SECONDS"), out var llmCallTimeout))
    settings = settings with { LlmCallTimeoutSeconds = llmCallTimeout };
if (ProgramHelpers.TryParsePositiveInt(Environment.GetEnvironmentVariable("LLM_STREAM_TIMEOUT_SECONDS"), out var llmStreamTimeout))
    settings = settings with { LlmStreamTimeoutSeconds = llmStreamTimeout };
// HARNESS_DWELL_MS (v5, presenter mode): >0 holds each harness engine's spotlight so a demo
// audience can watch the pipeline light up. 0/unset = OFF (real timing). Allow 0 explicitly.
if (int.TryParse(Environment.GetEnvironmentVariable("HARNESS_DWELL_MS"), out var harnessDwell) && harnessDwell >= 0)
    settings = settings with { HarnessDwellMs = harnessDwell };
// v8: OpenRouter provider routing + per-call-site reasoning_effort. Resolved ONCE here, so the
// mechanism never reads the environment itself — Tizen has no environment variables and fills the
// same record from goalflow.conf. Unset = LlmRouting.None = a request body identical to v7.
settings = settings with
{
    Routing = LlmRouting.FromEnvironment(
        Environment.GetEnvironmentVariable, loggerFactory.CreateLogger("llm-routing"))
};
var kernel = GoalAgent.BuildKernel(settings, provider);

WsClient? liveWs = null;
Func<AgentEvent, Task> emit = evt =>
{
    if (liveWs is not null)
    {
        return liveWs.SendAsync(evt);
    }

    Console.Error.WriteLine(ContractJson.Serialize(evt));
    return Task.CompletedTask;
};
var trace = new Trace(loggerFactory.CreateLogger<Trace>(), emit);
// Every accepted task transition streams a task_update. The cloud folds these into
// Agent Board's progress/next-step — the task DAG lives here, so this is the only
// way it can know. Wired here rather than in the DI block because the ledger and
// the trace sink are built at different times and this is where both exist.
var tasks = new TaskManager(
    loggerFactory.CreateLogger<TaskManager>(),
    (goal, task) => trace.TaskUpdateAsync(
        task,
        goal.ProgressPercent,
        goal.PendingTasks,
        tasksNextStep(goal)));
var agent = new GoalAgent(
    kernel,
    trace,
    provider.GetRequiredService<Grounding>(),
    provider.GetRequiredService<SafetyFilter>(),
    provider.GetRequiredService<RepeatReadFilter>(),
    provider.GetRequiredService<ToolRoundFilter>(),
    provider.GetRequiredService<ApprovalCoordinator>(),
    provider.GetRequiredService<MonitorAdapt>(),
    provider.GetRequiredService<CapabilityManager>(),
    tasks,
    provider.GetRequiredService<PrecheckEngine>(),
    provider.GetRequiredService<IClock>(),
    loggerFactory.CreateLogger<GoalAgent>(),
    settings);

// M0 VERIFICATION GATE (dev tool, not a demo path): print the deterministic
// surface of the kernel so a refactor can be proven behavior-neutral.
//   line 1  : the capabilities frame (pure reflection — no LLM, no network)
//   line 2+ : one Module.Function per grounding tool, IN THE ORDER the planner
//             hands them to the model.
// Diffed against verify/m0/*.golden by verify/m0/check.sh. Needs no real API
// key: BuildKernel only configures the connector, it never calls out.
if (options.DumpCapabilities)
{
    Console.Out.WriteLine(ContractJson.Serialize(provider.GetRequiredService<CapabilityManager>().BuildCapabilitiesMessage(kernel)));
    foreach (var fn in agent.GroundingFunctions())
    {
        Console.Out.WriteLine($"{fn.PluginName}.{fn.Name}");
    }

    return;
}

// M1 VERIFICATION GATE (dev tool): prove two concurrent goals cannot see each
// other's safety policy. Deterministic, no LLM — it drives the filter's real
// scope lookup, the same one the kernel pipeline uses.
if (options.VerifyPolicyIsolation)
{
    Environment.ExitCode = await ProgramHelpers.VerifyPolicyIsolationAsync(provider.GetRequiredService<SafetyFilter>());
    return;
}

if (options.VerifySafetyRules)
{
    Environment.ExitCode = ProgramHelpers.VerifySafetyRules(provider.GetRequiredService<SafetyFilter>());
    return;
}

if (options.VerifyGrades)
{
    Environment.ExitCode = ProgramHelpers.VerifyGrades(provider);
    return;
}

// v6-M2 GATE: the cap the planner is told about IS the cap it will be enforced
// against — one number, from the account, per goal.
if (options.VerifyActivePolicy)
{
    Environment.ExitCode = await ProgramHelpers.VerifyActivePolicyAsync(
        provider.GetRequiredService<ArmedPolicies>(),
        provider.GetRequiredService<BudgetPlugin>(),
        options.DataDir);
    return;
}

// v6-M3 GATE: two goals share one wallet — approving one order narrows the other
// goal's ceiling, and the other goal notices. MUTATES the world (it places an
// order), so run it against a throwaway --data dir.
if (options.VerifyEnvelope)
{
    Environment.ExitCode = await ProgramHelpers.VerifyEnvelopeAsync(provider, options.DataDir);
    return;
}

// v7-M2 GATE: one Advance day reports two changes and opens one approval. Read-only
// (it observes; it does not adapt or execute), so it needs no throwaway --data dir.
if (options.VerifyDayTick)
{
    Environment.ExitCode = await ProgramHelpers.VerifyDayTickAsync(provider);
    return;
}

// v7-M3 GATE: a thinking step is whole, and plain narration is byte-identical to v6.
// Needs the kernel (it drives a real blocked call through the real filter).
if (options.VerifyThinkingSteps)
{
    Environment.ExitCode = await ProgramHelpers.VerifyThinkingStepsAsync(provider);
    return;
}

// v7-M4 GATE: the home-away goal can reach what its plan promises. MUTATES the world
// (it really holds and resumes a delivery, and schedules a clean) — throwaway --data dir.
if (options.VerifyAwayCapabilities)
{
    Environment.ExitCode = await ProgramHelpers.VerifyAwayCapabilitiesAsync(provider);
    return;
}

// v7-M5 GATE: the one path that changes a plan without asking. Read-only.
if (options.VerifyCrossGoal)
{
    Environment.ExitCode = await ProgramHelpers.VerifyCrossGoalAsync(provider);
    return;
}

// v7 GATE: ...and the tick that comes after it must not undo it. Read-only.
if (options.VerifyAwayImmune)
{
    Environment.ExitCode = await ProgramHelpers.VerifyAwayImmuneAsync(provider);
    return;
}

// v6 GATE: the last gate before a real side effect must report a refusal AS a
// refusal. Needs the agent (it drives the real approval path), so it sits after the
// kernel is built. MUTATES the world (the allowed proposal really runs) — use a
// throwaway --data dir.
if (options.VerifyApprovalBlock)
{
    Environment.ExitCode = await ProgramHelpers.VerifyApprovalBlockAsync(provider, agent, tasks);
    return;
}

// The goal's next step: the frontier task's title — what Agent Board shows as
// "Next Step". Null once nothing is left to do.
static string? tasksNextStep(GoalRecord goal)
    => goal.Tasks.FirstOrDefault(t => !t.IsTerminal && t.State != TaskState.Monitoring)?.Title;

if (options.VerifyTaskLifecycle)
{
    Environment.ExitCode = await ProgramHelpers.VerifyTaskLifecycleAsync(loggerFactory);
    return;
}

if (options.VerifyPrechecks)
{
    Environment.ExitCode = await ProgramHelpers.VerifyPrechecksAsync(provider, options.DataDir);
    return;
}

if (options.VerifyRepeatReads)
{
    Environment.ExitCode = await ProgramHelpers.VerifyRepeatReadsAsync(provider, loggerFactory);
    return;
}

if (options.VerifyRequestShape)
{
    Environment.ExitCode = await ProgramHelpers.VerifyRequestShapeAsync(loggerFactory);
    return;
}

if (options.VerifyDeadline)
{
    Environment.ExitCode = await ProgramHelpers.VerifyDeadlineAsync(loggerFactory);
    return;
}

if (options.VerifyTraceIsolation)
{
    Environment.ExitCode = await ProgramHelpers.VerifyTraceIsolationAsync(loggerFactory);
    return;
}

if (options.ConnectUrl is { } url)
{
    var deviceId = ProgramHelpers.ResolveDeviceId(options.DeviceId, options.DataDir);
    var deviceName = ProgramHelpers.ResolveDeviceName(options.DeviceName, deviceId);
    loggerFactory.CreateLogger("Connect").LogInformation("device_id={DeviceId} device_name={DeviceName}", deviceId, deviceName);
    await using var ws = new WsClient(new Uri(url), loggerFactory.CreateLogger<WsClient>(), deviceId, deviceName);
    liveWs = ws;
    var capabilities = provider.GetRequiredService<CapabilityManager>().BuildCapabilitiesMessage(kernel);
    var connectLogger = loggerFactory.CreateLogger("Connect");
    await ws.ConnectAsync(capabilities);

    // Handle each frame on a BACKGROUND task so the receive loop keeps pumping —
    // planning takes 30-60s of LLM calls, and blocking the loop here means the
    // device can't answer WS pings, so the cloud's keepalive closes the socket
    // mid-plan. Fire-and-forget with error logging; WsClient.SendAsync is
    // serialized by its own send lock.
    ws.FrameReceived += (type, raw) =>
    {
        _ = Task.Run(async () =>
        {
            try
            {
                switch (type)
                {
                    case MessageTypes.Dispatch:
                        await ws.SendAsync(await agent.RunAsync(ContractJson.Deserialize<Dispatch>(raw)));
                        break;
                    case MessageTypes.Approval:
                        await ws.SendAsync(await agent.ApplyApprovalAsync(ContractJson.Deserialize<Approval>(raw)));
                        break;
                    case MessageTypes.Control:
                        var control = ContractJson.Deserialize<Control>(raw);
                        if (string.IsNullOrEmpty(control.GoalId) && control.Command != ControlCommands.TriggerEvent)
                        {
                            // WORLD-level tick (v3.2): advance the clock once, fan out to
                            // every active goal, and summarise the day's world events.
                            var world = await agent.HandleWorldControlAsync(control);
                            foreach (var s in world.Statuses) await ws.SendAsync(s);
                            foreach (var p in world.Proposals) await ws.SendAsync(p);
                            if (world.DayAdvanced is not null) await ws.SendAsync(world.DayAdvanced);
                        }
                        else
                        {
                            // Per-goal control (a trigger_event) — the older scoped path.
                            var (status, proposal) = await agent.HandleControlAsync(control);
                            await ws.SendAsync(status);
                            if (proposal is not null) await ws.SendAsync(proposal);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                connectLogger.LogError(ex, "frame handling failed for {Type}", type);
            }
        });
        return Task.CompletedTask;
    };
    await ws.RunReceiveLoopAsync();
}
else
{
    if (options.SimulateWeek || options.SimulateGuest)
    {
        await ProgramHelpers.RunSustainSimulationAsync(options, agent, provider.GetRequiredService<IClock>());
        return;
    }

    var dispatch = options.ContractPath is { } path
        ? ProgramHelpers.LoadDispatch(path, provider.GetRequiredService<IClock>())
        : ProgramHelpers.BuildLocalDispatch(options.Goal ?? throw new ArgumentException("Pass --contract, --goal, or --connect."), options.Domain, provider.GetRequiredService<IClock>());

    var plan = await agent.RunAsync(dispatch);
    Console.Out.WriteLine(ContractJson.Serialize(plan));

    if (options.ApprovalPath is { } approvalPath)
    {
        var approval = ContractJson.Deserialize<Approval>(File.ReadAllText(approvalPath));
        var status = await agent.ApplyApprovalAsync(approval);
        Console.Error.WriteLine(ContractJson.Serialize(status));
        var replay = await agent.ApplyApprovalAsync(approval);
        Console.Error.WriteLine(ContractJson.Serialize(replay));
    }
}

/// <summary>Parsed command-line options for the v2 entry point.</summary>
internal sealed record CliOptions
{
    /// <summary>--goal "..." — natural-language goal, dispatched locally.</summary>
    public string? Goal { get; init; }

    /// <summary>--domain — use-case name for --goal mode (default meal_plan).</summary>
    public string Domain { get; init; } = "meal_plan";

    /// <summary>--contract &lt;file&gt; — run a dispatch frame from disk.</summary>
    public string? ContractPath { get; init; }

    /// <summary>--approval &lt;file&gt; — apply an approval after the one-shot plan, then replay it.</summary>
    public string? ApprovalPath { get; init; }

    /// <summary>--connect &lt;ws url&gt; — live cloud session.</summary>
    public string? ConnectUrl { get; init; }

    /// <summary>--date &lt;ISO&gt; — start a SimulatedClock here. Null = real today (SystemClock).</summary>
    public string? Date { get; init; }

    /// <summary>--data &lt;dir&gt; — mock world directory (default ./data).</summary>
    public string DataDir { get; init; } = "data";

    /// <summary>--device-id &lt;id&gt; — pairing key (else $DEVICE_ID, else a persistent self-generated UUID).</summary>
    public string? DeviceId { get; init; }

    /// <summary>--device-name &lt;name&gt; — human label shown in the UI device picker.</summary>
    public string? DeviceName { get; init; }

    /// <summary>--verbose — debug-level logging.</summary>
    public bool Verbose { get; init; }

    /// <summary>--simulate-week — plan the meal contract, then advance weekdays and print sustain frames.</summary>
    public bool SimulateWeek { get; init; }

    /// <summary>--simulate-guest — plan the guest contract, then advance to the guest adaptation trigger.</summary>
    public bool SimulateGuest { get; init; }

    /// <summary>--dump-capabilities — print the kernel's deterministic surface and exit (M0 gate; see verify/m0/).</summary>
    public bool DumpCapabilities { get; init; }

    /// <summary>--verify-policy-isolation — assert two concurrent goals cannot see each other's safety policy (M1 gate).</summary>
    public bool VerifyPolicyIsolation { get; init; }

    /// <summary>--verify-safety-rules — assert the declarative rules block/allow the right things (M1 gate).</summary>
    public bool VerifySafetyRules { get; init; }

    /// <summary>--verify-grades — assert the grade ratchet holds and AX is unproposable (M1 gate).</summary>
    public bool VerifyGrades { get; init; }

    /// <summary>--verify-active-policy — assert the budget cap comes from the goal's armed policy, not device data (v6-M2 gate).</summary>
    public bool VerifyActivePolicy { get; init; }

    /// <summary>--verify-envelope — assert one goal's approved order narrows another's ceiling (v6-M3 gate).</summary>
    public bool VerifyEnvelope { get; init; }

    /// <summary>--verify-day-tick — assert one tick reports two changes and opens one approval (v7-M2 gate).</summary>
    public bool VerifyDayTick { get; init; }

    /// <summary>--verify-thinking-steps — assert a step is whole and narration is v6-identical (v7-M3 gate).</summary>
    public bool VerifyThinkingSteps { get; init; }

    /// <summary>--verify-away-immune — assert a world event cannot re-plan a day marked away (v7 gate).</summary>
    public bool VerifyAwayImmune { get; init; }

    /// <summary>--verify-away-capabilities — assert Act 3's steps are reachable and graded (v7-M4 gate).</summary>
    public bool VerifyAwayCapabilities { get; init; }

    /// <summary>--verify-cross-goal — assert an un-asked re-plan arms from the account and applies once (v7-M5 gate).</summary>
    public bool VerifyCrossGoal { get; init; }

    /// <summary>--verify-approval-block — assert a refused proposal is reported blocked, not executed (v6 gate).</summary>
    public bool VerifyApprovalBlock { get; init; }

    /// <summary>--verify-task-lifecycle — assert the task DAG, legal moves and derived progress (M2 gate).</summary>
    public bool VerifyTaskLifecycle { get; init; }

    /// <summary>--verify-prechecks — assert the runtime gates pass, block and defer correctly (M3 gate).</summary>
    public bool VerifyPrechecks { get; init; }

    /// <summary>--verify-trace-isolation — assert concurrent goals don't collide on goal_id/seq (M5 gate).</summary>
    public bool VerifyTraceIsolation { get; init; }

    /// <summary>--verify-repeat-reads — no read is asked twice, and an unsatisfiable query says so (v7.1 gate 28).</summary>
    public bool VerifyRepeatReads { get; init; }

    /// <summary>--verify-deadline — assert a stalled provider stream aborts rather than wedging a goal (M6 gate).</summary>
    public bool VerifyDeadline { get; init; }
    public bool VerifyRequestShape { get; init; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} requires a value.");
                return args[++i];
            }

            // Optional value: consume the next arg only if it isn't another flag.
            string? NextOptional()
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return null;
                return args[++i];
            }

            options = args[i] switch
            {
                "--goal" => options with { Goal = Next() },
                "--domain" => options with { Domain = Next() },
                "--contract" => options with { ContractPath = Next() },
                "--approval" => options with { ApprovalPath = Next() },
                // URL is optional: --connect <url>, else $WS_URL, else the local default.
                "--connect" => options with
                {
                    ConnectUrl = NextOptional()
                        ?? Environment.GetEnvironmentVariable("WS_URL")
                        ?? "ws://localhost:8000/ws",
                },
                "--date" => options with { Date = Next() },
                "--data" => options with { DataDir = Next() },
                "--device-id" => options with { DeviceId = Next() },
                "--device-name" => options with { DeviceName = Next() },
                "--verbose" => options with { Verbose = true },
                "--simulate-week" => options with { SimulateWeek = true, Domain = "meal_plan" },
                "--simulate-guest" => options with { SimulateGuest = true, Domain = "guest_dinner" },
                "--dump-capabilities" => options with { DumpCapabilities = true },
                "--verify-policy-isolation" => options with { VerifyPolicyIsolation = true },
                "--verify-safety-rules" => options with { VerifySafetyRules = true },
                "--verify-grades" => options with { VerifyGrades = true },
                "--verify-active-policy" => options with { VerifyActivePolicy = true },
                "--verify-envelope" => options with { VerifyEnvelope = true },
                "--verify-day-tick" => options with { VerifyDayTick = true },
                "--verify-thinking-steps" => options with { VerifyThinkingSteps = true },
                "--verify-away-capabilities" => options with { VerifyAwayCapabilities = true },
                "--verify-cross-goal" => options with { VerifyCrossGoal = true },
                "--verify-away-immune" => options with { VerifyAwayImmune = true },
                "--verify-approval-block" => options with { VerifyApprovalBlock = true },
                "--verify-task-lifecycle" => options with { VerifyTaskLifecycle = true },
                "--verify-prechecks" => options with { VerifyPrechecks = true },
                "--verify-trace-isolation" => options with { VerifyTraceIsolation = true },
                "--verify-repeat-reads" => options with { VerifyRepeatReads = true },
                "--verify-deadline" => options with { VerifyDeadline = true },
                "--verify-request-shape" => options with { VerifyRequestShape = true },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'.")
            };
        }

        return options;
    }
}

/// <summary>
/// Minimal KEY=VALUE .env loader (BCL only; missing file is fine).
///
/// <para>
/// THE FILE DOES NOT OVERRIDE THE ENVIRONMENT. It used to, unconditionally, which made
/// <c>FOO=bar dotnet run …</c> silently do nothing for any key that also appeared in
/// <c>.env</c> — the command looked like it worked, the value was replaced before anything
/// read it, and there was no message. It cost a bad measurement: a run exported with a
/// <c>:nitro</c> model id was quietly executed with the plain one from the file and came back
/// at 186s, looking like evidence about Semantic Kernel rather than about this loader.
/// </para>
///
/// <para>
/// It is also the precedence Tizen already uses — <c>DeviceConfig.Get</c> reads the
/// environment first and falls back to <c>goalflow.conf</c>. Having the two devices disagree
/// about which source wins is exactly the kind of difference that makes a port behave
/// differently from the box it was tested on.
/// </para>
/// </summary>
internal static class DotEnv
{
    public static void Load(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim().Trim('"');
            // Already set for real? Leave it. A value on the command line is the more
            // deliberate of the two.
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 })
            {
                continue;
            }
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

internal static class ProgramHelpers
{
/// <summary>
/// M5 GATE: trace isolation under concurrency.
///
/// <para>
/// Every agent_event carries a goal_id and a seq, and the UI DROPS any frame whose
/// seq isn't greater than the last it saw for that goal. So a shared counter isn't
/// cosmetic: goal B starting used to reset seq to 0 and re-pin the goal id, and
/// goal A's remaining events then streamed under B's id with a seq that had gone
/// backwards — the UI silently discarded them and A's plan stopped appearing, with
/// no error anywhere.
/// </para>
///
/// <para>
/// Two goals narrate concurrently here, interleaved deliberately. The assertion is
/// what the UI actually requires: every goal's frames carry ITS id, and its seqs
/// are strictly increasing from 1.
/// </para>
/// </summary>
public static async Task<int> VerifyTraceIsolationAsync(ILoggerFactory loggerFactory)
{
    var frames = new System.Collections.Concurrent.ConcurrentBag<AgentEvent>();
    var trace = new Trace(loggerFactory.CreateLogger<Trace>(), evt =>
    {
        frames.Add(evt);
        return Task.CompletedTask;
    });

    // A RENDEZVOUS, not a one-way signal: each goal announces it is inside its scope
    // and then waits for the other. A single TCS is not enough — `await` on an
    // already-completed task continues SYNCHRONOUSLY, so the first goal would run to
    // completion before the second even started, and a shared scope would sail
    // through. (It did: this gate passed against a deliberately shared scope until
    // the barrier was made two-way.)
    var arrived = new[] { new TaskCompletionSource(), new TaskCompletionSource() };

    async Task Narrate(int index, string goalId, int count)
    {
        using var scope = trace.BeginGoalScope(goalId, $"corr-{goalId}");
        arrived[index].SetResult();
        await arrived[1 - index].Task;

        for (var i = 0; i < count; i++)
        {
            await trace.PhaseAsync("planning");
            // Force the two flows to interleave rather than run back to back.
            await Task.Delay(1);
            await trace.ThinkingAsync($"{goalId} step {i}");
        }
    }

    await Task.WhenAll(
        Task.Run(() => Narrate(0, "goal-a", 5)),
        Task.Run(() => Narrate(1, "goal-b", 5)));

    var failures = new List<string>();
    foreach (var goalId in new[] { "goal-a", "goal-b" })
    {
        var mine = frames.Where(f => f.GoalId == goalId).OrderBy(f => f.Seq).ToArray();
        if (mine.Length != 10)
        {
            failures.Add($"{goalId}: emitted 10 frames, {mine.Length} carry its goal_id — the rest went out under another goal's id");
        }

        var seqs = mine.Select(f => f.Seq).ToArray();
        if (seqs.Distinct().Count() != seqs.Length)
        {
            failures.Add($"{goalId}: duplicate seq — the UI drops the repeats");
        }

        if (seqs.Length > 0 && (seqs[0] != 1 || !seqs.SequenceEqual(Enumerable.Range(1, seqs.Length))))
        {
            failures.Add($"{goalId}: seq must run 1..n per goal, got [{string.Join(",", seqs)}]");
        }

        if (mine.Any(f => f.CorrelationId != $"corr-{goalId}"))
        {
            failures.Add($"{goalId}: a frame carries another goal's correlation_id");
        }
    }

    foreach (var failure in failures) Console.Error.WriteLine($"  FAIL {failure}");
    Console.Out.WriteLine(failures.Count == 0 ? "gate 11 (trace isolation): PASS" : $"gate 11 FAIL: {failures.Count}");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// v7.1 GATE 28: the same read is never asked twice, and a tool that cannot satisfy a
/// query says so.
///
/// <para>
/// WHAT THIS IS ABOUT. Grounding is one streaming call with auto-invoke, so every tool
/// call inside it costs a round-trip to the model. A measured meal plan spent four and a
/// half minutes calling <c>Recipes.FindRecipes</c> ten-plus times with arguments that
/// differed only in the ORDER of the tag list — because the household prefers white meat,
/// the box is entirely vegetarian, and the old filter answered a query it could not
/// satisfy by returning everything in an unchanged order. Silence that looks like success
/// is the worst thing a tool can hand an agent: its only recourse is to ask again.
/// </para>
///
/// <para>
/// NO LLM HERE. The filter is exercised through <c>kernel.InvokeAsync</c>, which runs the
/// real invocation pipeline, so this gate tests the shipped path and not a mock of it.
/// </para>
/// </summary>
public static async Task<int> VerifyRepeatReadsAsync(IServiceProvider provider, ILoggerFactory loggerFactory)
{
    var failures = new List<string>();
    void Check(bool ok, string what) { if (!ok) { failures.Add(what); Console.Error.WriteLine($"  FAIL {what}"); } }

    // --- half one: FindRecipes reports what it could not match ---
    var recipes = new RecipePlugin(provider.GetRequiredService<IProductApiAdapter>());

    // `high_protein` is the exact word the planner reached for and the box does not use
    // (its tag is `more_protein`); `soy_free` is simply invented. Both must come back named.
    var hunted = await recipes.FindRecipes(["high_protein", "soy_free", "quick_prep"]);
    var hdoc = System.Text.Json.Nodes.JsonNode.Parse(hunted)!.AsObject();
    var unmatched = hdoc["unmatched_tags"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
    Check(unmatched.Contains("high_protein") && unmatched.Contains("soy_free"),
        $"tags that exist in NO recipe are named back to the caller — got [{string.Join(",", unmatched)}]");
    Check(hdoc["note"] is not null,
        "an unsatisfiable query carries a note telling the caller not to search again — without it the model rephrases and loops");
    Check(hdoc["available_tags"]!.AsArray().Count > 0,
        "the reply publishes the box's real tag vocabulary, so the NEXT call can use words that exist");
    Check(hdoc["recipes"]!.AsArray().Count > 0,
        "an unmatched query still returns the box — the caller needs the facts, not only the refusal");

    var real = await recipes.FindRecipes(["quick_prep"]);
    var rdoc = System.Text.Json.Nodes.JsonNode.Parse(real)!.AsObject();
    Check(rdoc["matched_tags"]!.AsArray().Count == 1 && rdoc["unmatched_tags"]!.AsArray().Count == 0,
        "a tag that DOES exist is reported as matched, and raises no note");
    Check(rdoc["note"] is null, "a satisfiable query is not lectured");

    // v7.2 SEED: the household's preference must have something to prefer AND something to
    // turn down. Through v7 every recipe was vegetarian, so `prefer_white_meat` could not
    // move a single dinner and the considered/rejected line had nothing true to say.
    var pref = await recipes.FindRecipes(["white_meat"]);
    var pdoc = System.Text.Json.Nodes.JsonNode.Parse(pref)!.AsObject();
    Check(pdoc["unmatched_tags"]!.AsArray().Count == 0,
        "white_meat is a REAL tag now — the standing household preference can actually bite");
    var box = pdoc["recipes"]!.AsArray().Select(n => n!.AsObject()).ToArray();
    string[] TagsOf(System.Text.Json.Nodes.JsonObject r) =>
        r["tags"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
    Check(box.Count(r => TagsOf(r).Contains("white_meat")) >= 3,
        "the box carries white-meat dishes, so the preference has something to CHOOSE");
    Check(box.Count(r => TagsOf(r).Contains("red_meat")) >= 2,
        "...and red-meat dishes, so it has something to REJECT — a preference that only ever agrees is undemonstrable");
    Check(TagsOf(box[0]).Contains("white_meat"),
        "preferring white_meat sorts a white-meat dish to the front");
    Check(box.All(r => !TagsOf(r).Contains("pork")) && box.All(r => !r["ingredients"]!.AsArray()
            .Any(i => i!.GetValue<string>().Contains("pork", StringComparison.OrdinalIgnoreCase))),
        "no pork in the seed — that 'no' belongs to the hard rule, and a hard rule must not need luck");

    // --- half two: the repeat guard, through the real kernel pipeline ---
    var filter = new RepeatReadFilter(loggerFactory.CreateLogger<RepeatReadFilter>());
    filter.Reset();

    var runs = 0;
    var builder = Kernel.CreateBuilder();
    builder.Services.AddSingleton<IFunctionInvocationFilter>(filter);
    var kernel = builder.Build();
    kernel.Plugins.AddFromFunctions("Probe", [
        KernelFunctionFactory.CreateFromMethod(
            (string tags) => { runs++; return $"{{\"asked\":\"{tags}\"}}"; },
            functionName: "Read")
    ]);

    var args = new KernelArguments { ["tags"] = "[\"a\",\"b\"]" };
    var first = (await kernel.InvokeAsync("Probe", "Read", args)).GetValue<string>() ?? "";
    var again = (await kernel.InvokeAsync("Probe", "Read", args)).GetValue<string>() ?? "";
    Check(runs == 1, $"an identical read runs the tool ONCE — it ran {runs} times");
    Check(!first.Contains("repeat_of_previous_call"), "the first call is answered by the tool, untouched");
    Check(again.Contains("repeat_of_previous_call") && again.Contains("\"asked\""),
        "the repeat is answered from the memo AND still carries the data — a bare scolding would invite another call");

    // The loop this gate exists for permuted the tag list, so order must not create a
    // "new" question. This is the assertion that would have caught the real bug.
    await kernel.InvokeAsync("Probe", "Read", new KernelArguments { ["tags"] = "[\"b\",\"a\"]" });
    Check(runs == 1, $"the SAME tags in a different ORDER is the same question — the tool ran {runs} times");

    // A genuinely different question must still get through, or the guard is a muzzle.
    await kernel.InvokeAsync("Probe", "Read", new KernelArguments { ["tags"] = "[\"c\"]" });
    Check(runs == 2, $"a different question still reaches the tool — ran {runs} times, expected 2");

    Check(filter.SuppressedCount == 2, $"the filter counts what it suppressed — got {filter.SuppressedCount}");

    // A new goal starts with no memory, or yesterday's world answers for today's.
    filter.Reset();
    await kernel.InvokeAsync("Probe", "Read", args);
    Check(runs == 3, $"Reset() clears the memo for the next goal — ran {runs} times, expected 3");

    Console.Out.WriteLine(failures.Count == 0
        ? "gate 28 (no read asked twice; an unsatisfiable query says so): PASS"
        : $"gate 28 FAIL: {failures.Count}");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// M3 GATE: the Pre-check Engine — is the world ready?
///
/// <para>
/// Drives the probes against a REAL device_state.json in a temp dir, flipping
/// flags to force each outcome. The failure paths are the point: a gate that only
/// ever sees a healthy world proves nothing, because passing is also what a probe
/// that does nothing does.
/// </para>
/// </summary>
public static async Task<int> VerifyPrechecksAsync(IServiceProvider provider, string dataDir)
{
    var failures = new List<string>();
    void Check(bool ok, string what) { if (!ok) failures.Add(what); }

    var statePath = Path.Combine(dataDir, "device_state.json");
    var pristine = await File.ReadAllTextAsync(statePath);
    var engine = provider.GetRequiredService<PrecheckEngine>();
    var dispatch = ProgramHelpers.BuildLocalDispatch("verify prechecks", "verify", new SimulatedClock());
    var preheat = new ProposalItem { ProposalId = "p1", Action = "preheat", Module = "Appliance", Function = "PreheatOven", Tier = ApprovalTiers.Light };
    var order = new ProposalItem { ProposalId = "p2", Action = "order", Module = "ShoppingList", Function = "PlaceOrder", Tier = ApprovalTiers.Firm };
    var read = new ProposalItem { ProposalId = "p3", Action = "list", Module = "Inventory", Function = "ListItems", Tier = ApprovalTiers.Auto };

    async Task SetState(string json) => await File.WriteAllTextAsync(statePath, json);
    async Task Flip(string path, bool value)
    {
        var node = JsonNode.Parse(pristine)!.AsObject();
        var parts = path.Split('.');
        if (parts.Length == 1) node[parts[0]] = value;
        else node[parts[0]]![parts[1]] = value;
        await SetState(node.ToJsonString());
    }

    try
    {
        // A healthy world blocks nothing.
        await SetState(pristine);
        Check((await engine.RunForDispatchAsync(dispatch)).Ok, "a healthy world passes the goal gate");
        Check((await engine.RunForProposalAsync(preheat)).Ok, "a healthy world passes the oven's checks");

        // An unbound call has no checks — silence, not a fabricated dependency.
        var unbound = await engine.RunForProposalAsync(read);
        Check(unbound.Ok && unbound.Results.Count == 0, "a call with no bindings runs no checks");

        // GATE 1: the goal can't even start.
        await Flip("samsung_account", false);
        var signedOut = await engine.RunForDispatchAsync(dispatch);
        Check(!signedOut.Ok, "signed out blocks the goal gate");
        Check(signedOut.Remediation.Contains("sign in"), $"the reason must be actionable, got: {signedOut.Remediation}");

        // GATE 2: the parameterized probe — one appliance offline, not the others.
        await Flip("appliances_online.oven", false);
        var ovenDown = await engine.RunForProposalAsync(preheat);
        Check(!ovenDown.Ok, "an offline oven defers PreheatOven");
        Check(ovenDown.Remediation.Contains("oven"), "the reason names the oven");
        Check((await engine.RunForProposalAsync(order)).Ok, "an offline OVEN must not block an unrelated ORDER");

        // Module-wide bindings are a floor: Appliance.* needs SmartThings, whatever
        // the function.
        await Flip("smartthings_connected", false);
        var noHub = await engine.RunForProposalAsync(preheat);
        Check(!noHub.Ok, "Appliance.* requires SmartThings — the module-wide rule applies");
        Check(noHub.Remediation.Contains("SmartThings"), "the reason names SmartThings");

        // Recovery: the whole point of "not yet" rather than "never".
        await SetState(pristine);
        Check((await engine.RunForProposalAsync(preheat)).Ok, "the check passes again once the world recovers");
    }
    finally
    {
        await File.WriteAllTextAsync(statePath, pristine);
    }

    foreach (var failure in failures) Console.Error.WriteLine($"  FAIL {failure}");
    Console.Out.WriteLine(failures.Count == 0 ? "gate 9 (prechecks): PASS" : $"gate 9 FAIL: {failures.Count}");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// M2 GATE: the task lifecycle — dependency order, legal moves, derived progress.
///
/// <para>
/// Agent Board reports progress %, next step and pending counts as FACTS. They
/// are only facts if the ledger underneath is sound, so this checks the three
/// things it rests on: a task never runs before what it depends on, an illegal
/// move is refused rather than silently applied, and progress is computed from
/// task state rather than guessed.
/// </para>
/// </summary>
public static async Task<int> VerifyTaskLifecycleAsync(ILoggerFactory loggerFactory)
{
    var tasks = new TaskManager(loggerFactory.CreateLogger<TaskManager>());
    var failures = new List<string>();
    void Check(bool ok, string what) { if (!ok) failures.Add(what); }

    // A four-task DAG: t1 → t2 → t3, and t4 waiting on both t2 and t3.
    //
    // DECLARED IN REVERSE DEPENDENCY ORDER, deliberately. Listed t1..t4, "the first
    // unfinished task" and "the first task whose deps are met" are the same answer,
    // so a NextReady that ignored dependencies entirely would still look correct —
    // the test would pass for the wrong reason. (It did: breaking dependency
    // resolution on purpose tripped only one assertion until this was reversed.)
    var dispatch = ProgramHelpers.BuildLocalDispatch("verify the task lifecycle", "verify", new SimulatedClock());
    var goal = tasks.CreateGoal(dispatch, [
        new TaskRecord { TaskId = "t4", GoalId = dispatch.GoalId, Title = "notify family", DependsOn = ["t2", "t3"] },
        new TaskRecord { TaskId = "t3", GoalId = dispatch.GoalId, Title = "build shopping list", DependsOn = ["t2"] },
        new TaskRecord { TaskId = "t2", GoalId = dispatch.GoalId, Title = "find recipes", DependsOn = ["t1"] },
        new TaskRecord { TaskId = "t1", GoalId = dispatch.GoalId, Title = "check inventory" },
    ], new JsonObject());

    Check(goal.ProgressPercent == 0, "a fresh goal is 0%");
    Check(goal.PendingTasks == 4, "a fresh goal has 4 pending");
    Check(tasks.NextReady(dispatch.GoalId)?.TaskId == "t1", "t1 is ready first (nothing blocks it)");

    // Dependencies gate the frontier: completing t1 releases t2, and only t2.
    await tasks.TransitionAsync(dispatch.GoalId, "t1", TaskState.Ready);
    await tasks.TransitionAsync(dispatch.GoalId, "t1", TaskState.Planning);
    await tasks.TransitionAsync(dispatch.GoalId, "t1", TaskState.Executing);
    await tasks.TransitionAsync(dispatch.GoalId, "t1", TaskState.Completed);
    Check(tasks.NextReady(dispatch.GoalId)?.TaskId == "t2", "completing t1 releases t2");
    Check(goal.ProgressPercent == 25, $"1 of 4 done is 25%, got {goal.ProgressPercent}");
    Check(goal.PendingTasks == 3, "3 pending after t1");

    // t4 must NOT be reachable while t3 is outstanding, even though t2 is done.
    await tasks.TransitionAsync(dispatch.GoalId, "t2", TaskState.Ready);
    await tasks.TransitionAsync(dispatch.GoalId, "t2", TaskState.Planning);
    await tasks.TransitionAsync(dispatch.GoalId, "t2", TaskState.Executing);
    await tasks.TransitionAsync(dispatch.GoalId, "t2", TaskState.Completed);
    Check(tasks.NextReady(dispatch.GoalId)?.TaskId == "t3", "t4 waits for BOTH its deps — t3 is next, not t4");

    // Illegal moves are refused, not applied. Completed is terminal.
    Check(!await tasks.TransitionAsync(dispatch.GoalId, "t1", TaskState.Planning), "Completed is terminal — no move out of it");
    Check(goal.Tasks.First(t => t.TaskId == "t1").State == TaskState.Completed, "a refused move must not mutate the task");
    Check(!await tasks.TransitionAsync(dispatch.GoalId, "t3", TaskState.Completed), "Created -> Completed skips the work — refused");
    Check(!await tasks.TransitionAsync(dispatch.GoalId, "nope", TaskState.Ready), "an unknown task id is refused, not created");

    // Retries are counted, and a retried task returns to the frontier.
    await tasks.TransitionAsync(dispatch.GoalId, "t3", TaskState.Ready);
    await tasks.TransitionAsync(dispatch.GoalId, "t3", TaskState.Planning);
    await tasks.TransitionAsync(dispatch.GoalId, "t3", TaskState.Retrying, "the store was unreachable");
    Check(goal.Tasks.First(t => t.TaskId == "t3").RetryCount == 1, "Retrying increments the retry count");
    Check(tasks.NextReady(dispatch.GoalId)?.TaskId == "t3", "a retrying task is still the frontier");

    // Monitoring counts as progress — the agent's work on that task is done and the
    // world is playing out. (t3 is mid-flight here, so this only checks the rule.)
    Check(goal.ProgressPercent == 50, $"2 of 4 done is 50%, got {goal.ProgressPercent}");

    // The percentage and the "n/m" line must never be able to disagree.
    Check(goal.WorkDone + goal.PendingTasks + goal.Tasks.Count(t => t.State is TaskState.Failed or TaskState.Cancelled) == goal.Tasks.Count,
        "WorkDone + Pending + terminal-failures must account for every task");

    // A failure reason is kept; failure is terminal and does NOT count as progress.
    await tasks.TransitionAsync(dispatch.GoalId, "t3", TaskState.Failed, "the oven never came back");
    Check(goal.Tasks.First(t => t.TaskId == "t3").FailureReason == "the oven never came back", "a failure keeps its reason");
    Check(goal.ProgressPercent == 50, $"a FAILED task is terminal but not progress: 2 of 4 = 50%, got {goal.ProgressPercent}");
    Check(tasks.NextReady(dispatch.GoalId) is null, "t4's dep failed, so nothing is ready — the goal is stuck, not silently done");
    Check(!goal.IsComplete, "a goal with an unreachable task is not complete");

    // ---- The DAG sanitizer: what protects the ledger from a bad decomposition ----
    // The decomposition is an LLM suggestion, so it can name a dependency that
    // doesn't exist, depend on itself, or form a cycle. A cycle is the dangerous
    // one: NextReady returns nothing and the goal looks alive forever.
    TaskRecord T(string id, params string[] deps) => new() { TaskId = id, GoalId = "g", Title = id, DependsOn = deps };

    var (unknown, r1) = TaskDag.Sanitize([T("t1", "nope"), T("t2", "t1")]);
    Check(unknown[0].DependsOn.Count == 0, "an unknown dependency is dropped, not kept");
    Check(r1.Any(r => r.Contains("unknown")), "dropping an unknown dep is reported");

    var (self, _) = TaskDag.Sanitize([T("t1", "t1")]);
    Check(self[0].DependsOn.Count == 0, "a self-dependency is dropped (it can never be satisfied)");

    var (cycle, r2) = TaskDag.Sanitize([T("t1", "t2"), T("t2", "t1")]);
    Check(cycle.Count == 2, "a cycle keeps both tasks — break the edge, not the goal");
    Check(r2.Any(r => r.Contains("cycle")), "breaking a cycle is reported");
    var cycleGoal = tasks.CreateGoal(
        ProgramHelpers.BuildLocalDispatch("cycle", "verify", new SimulatedClock()) with { GoalId = "cyc" },
        cycle.Select(t => t with { GoalId = "cyc" }).ToArray(), new JsonObject());
    Check(tasks.NextReady("cyc") is not null, "a repaired cycle must be RUNNABLE — else the goal hangs forever");

    var (capped, r3) = TaskDag.Sanitize(Enumerable.Range(1, 20).Select(i => T($"t{i}")).ToArray());
    Check(capped.Count == TaskDag.MaxTasks, $"20 tasks capped to {TaskDag.MaxTasks}");
    Check(r3.Any(r => r.Contains("capped")), "capping is reported, not silent");

    var (ordered, _) = TaskDag.Sanitize([T("t3", "t2"), T("t1"), T("t2", "t1")]);
    Check(ordered.Select(t => t.TaskId).SequenceEqual(["t1", "t2", "t3"]), "tasks come back in dependency order");

    foreach (var failure in failures) Console.Error.WriteLine($"  FAIL {failure}");
    Console.Out.WriteLine(failures.Count == 0 ? "gate 8 (task lifecycle + DAG): PASS" : $"gate 8 FAIL: {failures.Count}");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// M1 GATE: automation grades — the ratchet, and AX.
///
/// <para>
/// AX has no natural subject in this product yet (nothing the Family Hub does is
/// prohibited; the first one is the smart lock, in M7), so it is exercised here
/// through a throwaway policy rather than left as a mechanism nobody runs until
/// the demo. The ratchet is checked in BOTH directions, because a one-way check
/// would pass on a rule that rejects everything.
/// </para>
/// </summary>
public static int VerifyGrades(IServiceProvider provider)
{
    var descriptors = FamilyHubProduct.CreateDescriptors(provider);
    var failures = new List<string>();

    SafetyPolicy Policy(string overridesJson) => SafetyPolicy.Parse(
        JsonNode.Parse("{\"grades\":{\"overrides\":{" + overridesJson + "}},\"rules\":[]}")!.AsObject(), "<test>");

    // Intrinsic grades come from [SideEffect] with no config at all.
    var plain = new CapabilityManager(descriptors, Policy(""));
    void Expect(string module, string function, AutomationGrade? want, CapabilityManager mgr, string why)
    {
        var got = mgr.GradeOf(module, function);
        if (got != want) failures.Add($"{why}: {module}.{function} graded {got?.ToString() ?? "null"}, expected {want?.ToString() ?? "null"}");
    }

    Expect("ShoppingList", "PlaceOrder", AutomationGrade.A2, plain, "firm -> A2");
    Expect("ShoppingList", "Add", AutomationGrade.A1, plain, "light -> A1");
    Expect("Reminders", "Create", AutomationGrade.A0, plain, "auto -> A0");
    Expect("Inventory", "ListItems", null, plain, "a read is not an action");

    // TIGHTENING is allowed.
    var tightened = new CapabilityManager(descriptors, Policy("\"ShoppingList.Add\":\"A2\""));
    Expect("ShoppingList", "Add", AutomationGrade.A2, tightened, "policy may tighten A1 -> A2");

    // WEAKENING must throw, at construction, not at the call that matters.
    try
    {
        _ = new CapabilityManager(descriptors, Policy("\"ShoppingList.PlaceOrder\":\"A0\""));
        failures.Add("THE RATCHET DID NOT HOLD: policy weakened PlaceOrder A2 -> A0 and nothing threw");
    }
    catch (InvalidOperationException)
    {
        // expected
    }

    // AX: prohibited actions are never offered to the planner.
    var prohibited = new CapabilityManager(descriptors, Policy("\"ShoppingList.PlaceOrder\":\"AX\""));
    Expect("ShoppingList", "PlaceOrder", AutomationGrade.AX, prohibited, "policy may tighten A2 -> AX");
    if (prohibited.IsProposable("ShoppingList", "PlaceOrder")) failures.Add("an AX action must never be a proposal target");
    if (!prohibited.IsProposable("ShoppingList", "Add")) failures.Add("a non-AX action must stay proposable");
    if (plain.IsProposable("Budget", "GetBudgetStatus")) failures.Add("an unavailable plugin's function must not be proposable");
    if (plain.IsProposable("Inventory", "ListItems")) failures.Add("a read is not an action and must not be proposable");

    foreach (var failure in failures) Console.Error.WriteLine($"  FAIL {failure}");
    Console.Out.WriteLine(failures.Count == 0 ? "gate 7 (grades: ratchet + AX): PASS" : $"gate 7 FAIL: {failures.Count}");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// M1 GATE: the declarative safety rules block what they must and — just as
/// important — allow what they must not block.
///
/// <para>
/// The false-positive rows are the point. A naive contains() check is easy to
/// "strengthen" until a nut allergy blocks coconut and butternut squash, at which
/// point the family turns the agent off, so the over-blocking rows guard the fix
/// as much as the under-blocking ones do. The "peanut butter" row is the bug this
/// milestone fixed: an allergen of "peanuts" did not block it, because the plural
/// term is not a substring of the singular phrase.
/// </para>
/// </summary>
public static int VerifySafetyRules(SafetyFilter safety)
{
    var allergyNuts = new JsonObject { ["allergens"] = new JsonArray("nuts") };
    var allergyPeanuts = new JsonObject { ["allergens"] = new JsonArray("peanuts") };
    var noDairy = new JsonObject { ["dietary"] = new JsonArray("dairy") };
    var noPork = new JsonObject { ["dietary"] = new JsonArray("no_pork") };
    var budget = new JsonObject { ["budget_cap"] = 120.0 };
    var quiet = new JsonObject { ["quiet_hours"] = new JsonObject { ["start"] = "21:30", ["end"] = "07:00" } };
    // v6: the two window kinds the cloud now resolves PER DOMAIN. An energy goal
    // carries peak_hours; a vacation goal carries away_window; a dinner carries
    // neither, which is why the "no peak_hours on this goal" rows below matter as
    // much as the blocking ones — the scoping is half the design.
    var peak = new JsonObject { ["peak_hours"] = new JsonObject { ["start"] = "17:00", ["end"] = "21:00" } };
    // The away window BRACKETS TODAY, deliberately. With a window sitting in the
    // future, the "a bare 22:00 is not a date" row would pass no matter how loose the
    // date parsing was — today would fall outside the window and be allowed anyway.
    // Straddling today is what makes that row test the trap it was written for.
    var today = DateOnly.FromDateTime(DateTime.Today);
    string Day(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd");
    var away = new JsonObject { ["away_window"] = new JsonObject { ["start"] = Day(-1), ["end"] = Day(6) } };

    (string Label, JsonObject Hard, string Module, string Function, KernelArguments Args, bool ShouldBlock)[] cases =
    [
        // The fix: singular/plural and compound phrases.
        ("peanuts blocks 'peanut butter'",   allergyPeanuts, "ShoppingList", "Add", Items("peanut butter"), true),
        ("peanuts blocks 'peanuts'",         allergyPeanuts, "ShoppingList", "Add", Items("peanuts"), true),
        ("peanuts blocks 'roasted peanuts'", allergyPeanuts, "ShoppingList", "Add", Items("roasted peanuts"), true),
        ("nuts blocks 'cashews' (group)",    allergyNuts,    "ShoppingList", "Add", Items("cashews"), true),
        ("nuts blocks 'almond flour'",       allergyNuts,    "ShoppingList", "Add", Items("almond flour"), true),

        // The other half: not over-blocking. A "nuts" allergy must not veto these.
        ("nuts ALLOWS 'coconut milk'",       allergyNuts,    "ShoppingList", "Add", Items("coconut milk"), false),
        ("nuts ALLOWS 'butternut squash'",   allergyNuts,    "ShoppingList", "Add", Items("butternut squash"), false),
        ("nuts ALLOWS 'nutmeg'",             allergyNuts,    "ShoppingList", "Add", Items("nutmeg"), false),

        // Unchanged v2 behaviour, ported 1:1.
        ("dairy blocks 'whole milk'",        noDairy,        "ShoppingList", "Add", Items("whole milk"), true),
        ("dairy ALLOWS 'oat drink'",         noDairy,        "ShoppingList", "Add", Items("oat drink"), false),
        ("no_pork blocks 'bacon'",           noPork,         "ShoppingList", "Add", Items("bacon"), true),
        ("budget_cap blocks an over-spend",  budget,         "ShoppingList", "PlaceOrder", new KernelArguments { ["estimatedTotal"] = 130.0 }, true),
        ("budget_cap allows an under-spend", budget,         "ShoppingList", "PlaceOrder", new KernelArguments { ["estimatedTotal"] = 110.0 }, false),
        ("quiet_hours blocks a 22:00 run",   quiet,          "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = "22:00" }, true),
        ("quiet_hours allows an 18:00 run",  quiet,          "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = "18:00" }, false),
        // Rule bindings come from policy.json: the cap is bound to ShoppingList only.
        ("budget rule is NOT bound to Reminders", budget,    "Reminders",    "Create", new KernelArguments { ["estimatedTotal"] = 130.0 }, false),

        // v6 PEAK TARIFF — the same rule kind as quiet hours, a different window.
        ("peak_hours blocks an 18:00 run",   peak,           "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = "18:00" }, true),
        ("peak_hours allows a 23:00 run",    peak,           "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = "23:00" }, false),
        // THE SCOPING: 18:00 is inside the peak window, but a guest dinner's dispatch
        // does not carry peak_hours — so the same call that an energy goal must not
        // make is ordinary here. A rule that fired on every goal would have taken the
        // dinner demo down with it.
        ("no peak_hours on this goal -> 18:00 allowed", quiet, "Appliance",  "RunProgram", new KernelArguments { ["atTime"] = "18:00" }, false),
        ("peak rule is NOT bound to Notify", peak,           "Notify",       "Announce",   new KernelArguments { ["time"] = "18:00" }, false),

        // v6 AWAY WINDOW — the house is empty from yesterday until today+6.
        ("away blocks a mid-trip dishwasher run", away,      "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = $"{Day(2)}T09:00" }, true),
        ("away blocks a mid-trip announcement",   away,      "Notify",       "Announce",   new KernelArguments { ["date"] = Day(2) }, true),
        // Endpoints are EXCLUSIVE: the family is home part of both travel days, and
        // "run the dishwasher before you leave" is the vacation plan's own best move.
        // Blocking it would veto the plan the goal exists to produce.
        ("away ALLOWS the departure-day run",     away,      "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = $"{Day(-1)}T07:00" }, false),
        ("away ALLOWS the return-day run",        away,      "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = $"{Day(6)}T20:00" }, false),
        ("away ALLOWS a run before the trip",     away,      "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = $"{Day(-3)}T20:00" }, false),
        // The parse trap, and the reason the window above straddles today:
        // DateTime.TryParse resolves a bare "22:00" to TODAY — which IS inside this
        // window — so a loose parse would block a quiet-hours-shaped argument that
        // names no date at all. Only an ISO date prefix counts as a date.
        ("away ignores a time-only argument",     away,      "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = "22:00" }, false),
        // The same trap, long enough to get past a length check: DateTime.TryParse
        // turns "22:00:00.000" into TODAY at 22:00 and blocks a call that names no
        // date. This is the row that fails if the ISO-prefix parse is ever relaxed.
        ("away ignores a long time-only argument", away,     "Appliance",    "RunProgram", new KernelArguments { ["atTime"] = "22:00:00.000" }, false),
        ("away rule is NOT bound to ShoppingList", away,     "ShoppingList", "Add",        new KernelArguments { ["date"] = Day(2) }, false),
    ];

    var failures = 0;
    foreach (var (label, hard, module, function, args, shouldBlock) in cases)
    {
        var violation = safety.Check(hard, module, function, args);
        var blocked = violation is not null;
        if (blocked != shouldBlock)
        {
            failures++;
            Console.Error.WriteLine(shouldBlock
                ? $"  FAIL {label}: expected BLOCK, was allowed"
                : $"  FAIL {label}: expected ALLOW, was blocked ({violation})");
        }
    }

    Console.Out.WriteLine(failures == 0
        ? $"gate 6 (safety rules, {cases.Length} cases): PASS"
        : $"gate 6 FAIL: {failures}/{cases.Length} cases");
    return failures == 0 ? 0 : 1;

    static KernelArguments Items(params string[] items) => new() { ["items"] = items };
}

/// <summary>
/// v6 GATE: approving something the policy forbids comes back as BLOCKED, not executed.
///
/// <para>
/// THE BUG THIS PINS. Side-effecting tools are not exposed during planning, so the
/// window constraints (quiet hours, peak tariff, the away window) can only bite at
/// ACTUATION — the moment a person taps Approve. That path invoked the function, took
/// whatever came back, called MarkExecuted and reported <c>"executed"</c>. The filter
/// worked perfectly: the plugin never ran, and the refusal sat in a detail string
/// nobody reads. The user was told the action had happened.
/// </para>
///
/// <para>
/// A gate that blocks correctly and then reports success is worse than one that fails
/// loudly, so this drives the REAL <see cref="GoalAgent.ApplyApprovalAsync"/> — same
/// kernel, same filter, same ledger — and checks both halves: the refused proposal is
/// reported blocked and is NOT marked executed (re-applying it can never work), while
/// an allowed one alongside it still goes through.
/// </para>
/// </summary>
public static async Task<int> VerifyApprovalBlockAsync(IServiceProvider provider, GoalAgent agent, TaskManager tasks)
{
    var failures = new List<string>();
    var approvals = provider.GetRequiredService<ApprovalCoordinator>();
    var safety = provider.GetRequiredService<SafetyFilter>();
    var clock = provider.GetRequiredService<IClock>();

    // The family is away from tomorrow for a week.
    var away = new JsonObject
    {
        ["start"] = clock.Today.AddDays(1).ToString("yyyy-MM-dd"),
        ["end"] = clock.Today.AddDays(8).ToString("yyyy-MM-dd"),
    };
    var hard = new JsonObject { ["away_window"] = away };
    var dispatch = ProgramHelpers.BuildLocalDispatch("verify the approval gate", "vacation_prep", clock) with
    {
        GoalId = "approval-block",
        Constraints = new TaskConstraints { Hard = hard },
    };
    tasks.CreateGoal(dispatch, [new TaskRecord { TaskId = "t1", GoalId = dispatch.GoalId, Title = "prep" }], new JsonObject());

    // p1 runs the dishwasher mid-trip (forbidden); p2 adds to the shopping list (fine).
    approvals.Register(new ProposalItem
    {
        ProposalId = "p1",
        Action = "run the dishwasher while away",
        Module = "Appliance",
        Function = "RunProgram",
        Args = new JsonObject
        {
            ["appliance"] = "dishwasher",
            ["program"] = "eco",
            ["atTime"] = $"{clock.Today.AddDays(4):yyyy-MM-dd}T09:00",
        },
        Tier = ApprovalTiers.Firm,
        Reason = "cleanup",
    });
    approvals.Register(new ProposalItem
    {
        ProposalId = "p2",
        Action = "add tinned goods to the list",
        Module = "ShoppingList",
        Function = "Add",
        Args = new JsonObject { ["items"] = new JsonArray("rice"), ["reason"] = "restock" },
        Tier = ApprovalTiers.Light,
        Reason = "restock",
    });
    // p3 THROWS: the household has never had a magazine subscription, so the plugin's
    // Find raises. This is the commonest actuator failure there is — the model naming
    // something that does not exist — and it used to be fatal. The invoke was unguarded,
    // so the exception left the loop AND the handler: every later proposal was skipped,
    // nothing was marked executed, and the status frame that tells the cloud and the
    // board what happened was never sent. The goal went silent with one stack trace in
    // the device log, which is exactly how it was found.
    approvals.Register(new ProposalItem
    {
        ProposalId = "p3",
        Action = "hold the magazine subscription",
        Module = "Deliveries",
        Function = "Hold",
        Args = new JsonObject
        {
            ["delivery"] = "magazine subscription",
            ["until"] = $"{clock.Today.AddDays(9):yyyy-MM-dd}",
        },
        Tier = ApprovalTiers.Firm,
        Reason = "nobody is home to take it in",
    });

    using (safety.BeginGoal(dispatch.GoalId, hard))
    {
        var status = await agent.ApplyApprovalAsync(new Approval
        {
            GoalId = dispatch.GoalId,
            CorrelationId = "c-approval-block",
            Payload = new ApprovalPayload
            {
                Decisions =
                [
                    new ApprovalDecision { ProposalId = "p1", Approved = true },
                    new ApprovalDecision { ProposalId = "p3", Approved = true },
                    new ApprovalDecision { ProposalId = "p2", Approved = true },
                ],
            },
        });

        var executed = status.Payload?.Executed ?? [];
        var blocked = executed.FirstOrDefault(e => e.ProposalId == "p1");
        if (blocked is null)
        {
            failures.Add("the refused proposal must still be REPORTED — silence loses it entirely");
        }
        else
        {
            if (blocked.Result != ExecutionResults.BlockedSafety)
            {
                failures.Add($"a proposal the filter refused must come back blocked, got '{blocked.Result}' — the user is being told it happened");
            }

            if (blocked.Detail?.Contains("away_window", StringComparison.Ordinal) != true)
            {
                failures.Add($"the block must say WHICH constraint refused it, got '{blocked.Detail}'");
            }
        }

        if (approvals.ExecutedIds().Contains("p1"))
        {
            failures.Add("a blocked proposal must NOT be marked executed — unlike a deferred pre-check, re-applying it can never work");
        }

        // The actuator that THREW. Reported, not fatal.
        var threw = executed.FirstOrDefault(e => e.ProposalId == "p3");
        if (threw is null)
        {
            failures.Add("an actuator that threw must still be REPORTED — the goal used to go silent instead");
        }
        else
        {
            if (threw.Result != ExecutionResults.FailedActuator)
            {
                failures.Add($"a proposal whose actuator threw must come back failed, got '{threw.Result}'");
            }

            if (threw.Detail?.Contains("magazine subscription", StringComparison.Ordinal) != true)
            {
                failures.Add($"the failure must say WHAT could not be done, got '{threw.Detail}'");
            }
        }

        if (approvals.ExecutedIds().Contains("p3"))
        {
            failures.Add("a failed actuator must NOT be marked executed — the args were frozen at planning time, so re-applying fails identically");
        }

        var allowed = executed.FirstOrDefault(e => e.ProposalId == "p2");
        if (allowed?.Result != ExecutionResults.Executed)
        {
            failures.Add($"one blocked or failed proposal must not take the others down with it, got '{allowed?.Result}'");
        }

        if (!approvals.ExecutedIds().Contains("p2"))
        {
            failures.Add("the allowed proposal must be marked executed");
        }
    }

    foreach (var failure in failures) Console.Error.WriteLine($"  FAIL {failure}");
    Console.Out.WriteLine(failures.Count == 0
        ? "gate 21 (approval: a refusal is reported as a refusal): PASS"
        : $"gate 21 FAIL: {failures.Count}");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// v6-M3 GATE: two goals share one wallet.
///
/// <para>
/// Per-goal caps cannot see each other: a $200 party and a $120 grocery week each fit
/// their own ceiling and together blow a $600 month. The envelope is the shared pool,
/// and the proof it works is not that a number appears on a contract — it is that
/// APPROVING ONE GOAL'S ORDER NARROWS ANOTHER GOAL'S CEILING, and that the second goal
/// then notices.
/// </para>
///
/// <para>
/// Deterministic and offline: it drives the real resolver, the real armed-policy store
/// and the real observer against a throwaway copy of the world, with no LLM anywhere.
/// </para>
/// </summary>
/// <summary>
/// v7-M5 GATE: a constraint change re-arms from the ACCOUNT's block, and applies once.
///
/// <para>
/// This is the only path in the system that changes a plan without asking, which makes it
/// the one worth pinning hardest. Two things make it defensible and neither is visible in
/// the demo: the device re-arms from a block it was SENT (it does not author policy just
/// because it was told the household moved), and a change already applied is never applied
/// again however many times the frame arrives — a re-sent approval, a reconnect replaying
/// one, or a cloud that fans out twice.
/// </para>
///
/// <para>
/// Deterministic and offline. The RE-PLAN itself needs an LLM, so what is asserted here is
/// everything around it: the arming, the dedupe, and that the always-enforced rules survive
/// a re-dispatch rather than being reset by it.
/// </para>
/// </summary>
public static async Task<int> VerifyCrossGoalAsync(IServiceProvider provider)
{
    var failures = new List<string>();
    var armed = provider.GetRequiredService<ArmedPolicies>();
    var safety = provider.GetRequiredService<SafetyFilter>();

    var dispatched = new JsonObject { ["allergens"] = new JsonArray("peanuts") };
    using (armed.Arm("goal-week", dispatched, (JsonObject)dispatched.DeepClone()))
    {
        // 1. THE ACCOUNT OWNS THE POLICY. A new block replaces the DISPATCHED one, so a
        //    later re-resolve starts from what the account last said — not from whatever
        //    the device had narrowed it to, which would let its own arithmetic compound.
        var updated = new JsonObject
        {
            ["allergens"] = new JsonArray("peanuts"),
            ["away_window"] = new JsonObject { ["start"] = "2026-07-30", ["end"] = "2026-07-31" },
        };
        await safety.ReDispatchAsync("goal-week", (JsonObject)updated.DeepClone());

        var nowDispatched = armed.DispatchedFor("goal-week");
        if (nowDispatched?["away_window"] is null)
        {
            failures.Add($"the new block must become the DISPATCHED one, got {nowDispatched?.ToJsonString()}");
        }
        if (armed.ActiveHard()?["away_window"] is null)
        {
            failures.Add("…and must be what is enforced from here on");
        }
        if (nowDispatched?["allergens"] is null)
        {
            failures.Add("the always-enforced rules must survive a re-dispatch — this is not a reset");
        }

        // 2. IT DOES NOT ARM A GOAL THAT WAS NEVER ARMED. A constraint change for a goal
        //    this device is not running must be a no-op, not a policy conjured from a frame.
        await safety.ReDispatchAsync("goal-never-seen", (JsonObject)updated.DeepClone());
        if (armed.DispatchedFor("goal-never-seen") is not null)
        {
            failures.Add("a constraint change for an unknown goal must not arm one");
        }
    }

    // 3. APPLIED ONCE. The dedupe is a set on the goal record keyed by the change's stable
    //    key; the same steer arriving twice must be recognised as the same change.
    var record = new GoalRecord
    {
        Dispatch = new Dispatch
        {
            GoalId = "goal-week",
            CorrelationId = "c",
            Domain = "meal_plan",
            Objective = "plan my weekly meal",
            Constraints = new TaskConstraints { Hard = new JsonObject() },
            TimeWindow = new TimeWindow { Start = "2026-07-28", End = "2026-08-03" },
        },
        Tasks = [],
    };
    const string key = "constraints:abcd1234";
    if (!record.EmittedMaterialChanges.Add(key) || record.EmittedMaterialChanges.Add(key))
    {
        failures.Add("the same constraint change must be surfaced exactly once");
    }

    foreach (var f in failures) Console.WriteLine($"  FAIL {f}");
    Console.WriteLine("gate 25 (cross-goal: the account arms it, and it applies once): "
                      + (failures.Count == 0 ? "PASS" : $"FAIL: {failures.Count}"));
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// v7-M4 GATE: the home-away goal can actually reach what its plan promises.
///
/// <para>
/// A plan step is only real if a function exists behind it. Act 3 narrates pausing
/// deliveries, handing the house to SmartThings, arming security and coming back to a
/// clean house — so this pins that each of those is a callable thing with the right
/// grade, not a sentence the planner was told to write.
/// </para>
///
/// <para>
/// The row that matters most is the REFUSAL. Holding a repeat prescription to tidy up
/// the porch is the obvious failure of a goal told to "pause non-essential deliveries",
/// and it is not caught by reading: it is caught by the function saying no. Deterministic
/// code, same shape as the Safety engine, for the same reason.
/// </para>
/// </summary>
public static async Task<int> VerifyAwayCapabilitiesAsync(IServiceProvider provider)
{
    var failures = new List<string>();
    var deliveries = provider.GetRequiredService<DeliveriesPlugin>();
    var appliances = provider.GetRequiredService<ApplianceControlPlugin>();
    var clock = provider.GetRequiredService<IClock>();
    var until = clock.Today.AddDays(3).ToString("yyyy-MM-dd");

    // 1. THE REFUSAL. An essential delivery cannot be held, however it is asked for.
    try
    {
        await deliveries.Hold("repeat prescription", until);
        failures.Add("holding an essential delivery (medication) must be refused, and was not");
    }
    catch (InvalidOperationException ex)
    {
        if (!ex.Message.Contains("essential", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"the refusal must say WHY, got: {ex.Message}");
        }
    }

    // 2. A non-essential one holds, and records what it was held until — the away window
    //    is the whole point, so a hold with no end date is a subscription quietly killed.
    var held = JsonNode.Parse(await deliveries.Hold("milk subscription", until))?.AsObject();
    if (held?["status"]?.GetValue<string>() != "held" || held["until"]?.GetValue<string>() != until)
    {
        failures.Add($"a non-essential delivery must hold until a date, got {held?.ToJsonString()}");
    }

    // 3. …and comes back. Return readiness is not decoration: an unresumable hold means
    //    the family is still without milk a week after they got home.
    var resumed = JsonNode.Parse(await deliveries.Resume("milk subscription"))?.AsObject();
    if (resumed?["status"]?.GetValue<string>() != "resumed")
    {
        failures.Add($"a held delivery must resume, got {resumed?.ToJsonString()}");
    }
    var after = JsonNode.Parse(await deliveries.ListDeliveries())?.AsArray()
        .Select(n => n?.AsObject()).OfType<JsonObject>()
        .FirstOrDefault(d => d["id"]?.GetValue<string>() == "del-milk");
    if (after?["held"]?.GetValue<bool>() != false || after.ContainsKey("held_until"))
    {
        failures.Add($"resuming must clear both the flag and the date, got {after?.ToJsonString()}");
    }

    // 4. THE GRADES. Hold is A2 — an outward-facing commitment to a third party, not the
    //    same kind of act as adding milk to a list — so it must land as its own approval
    //    rather than riding the batch.
    var registry = provider.GetRequiredService<CapabilityManager>();
    var gradeKernel = Kernel.CreateBuilder().Build();
    gradeKernel.Plugins.AddFromObject(deliveries, "Deliveries");
    gradeKernel.Plugins.AddFromObject(provider.GetRequiredService<SecurityPlugin>(), "Security");
    var catalog = gradeKernel.Plugins
        .ToDictionary(p => p.Name, p => registry.DescribePlugin(p).Functions ?? []);

    foreach (var (module, function, wantTier) in new[]
    {
        ("Deliveries", "Hold", ApprovalTiers.Firm),
        ("Deliveries", "Resume", ApprovalTiers.Light),
        ("Security", "ArmSecurity", ApprovalTiers.Light),
    })
    {
        var fn = catalog.TryGetValue(module, out var fns)
            ? fns.FirstOrDefault(f => f.Name == function)
            : null;
        if (fn is null) failures.Add($"{module}.{function} must exist — Act 3 plans a step onto it");
        else if (fn.Tier != wantTier) failures.Add($"{module}.{function} must be tier {wantTier}, got {fn.Tier}");
    }

    // 5. THE ROBOT VACUUM EXISTS. ApplianceControlPlugin's own [Description] has
    //    advertised a vacuum since v2 while the world held none — and the planner reads
    //    that description, so an over-claim invites a step the device then cannot run.
    var list = JsonNode.Parse(await appliances.ListAppliances())?.AsArray();
    var rvc = list?.Select(n => n?.AsObject()).OfType<JsonObject>()
        .FirstOrDefault(a => a["id"]?.GetValue<string>() == "rvc");
    if (rvc is null)
    {
        failures.Add("the robot vacuum must exist — return readiness schedules a clean through Appliance.RunProgram");
    }
    else if (rvc["programs"]?.AsArray().Count is null or 0)
    {
        failures.Add("the vacuum needs programs, or Appliance.RunProgram refuses every clean it is asked for");
    }
    else
    {
        var program = rvc["programs"]!.AsArray()[0]!.GetValue<string>();
        var run = JsonNode.Parse(await appliances.RunProgram("rvc", program, $"{until}T11:00"))?.AsObject();
        if (run?["status"]?.GetValue<string>() != "scheduled")
        {
            failures.Add($"the vacuum must be schedulable through the EXISTING RunProgram, got {run?.ToJsonString()}");
        }
    }

    foreach (var f in failures) Console.WriteLine($"  FAIL {f}");
    Console.WriteLine("gate 24 (away capabilities: reachable, graded, refusing): " + (failures.Count == 0 ? "PASS" : $"FAIL: {failures.Count}"));
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// v7-M3 GATE: the run says what it DID, and a v6 client cannot tell the difference.
///
/// <para>
/// Two things are being pinned, and the second is the one that would break quietly.
/// First: a step is WHOLE — it carries its own headline and sub-line, and a client never
/// has to accumulate fragments or guess where one thought ends. Second: plain narration
/// is BYTE-IDENTICAL to what v6 emitted. The whole design rests on `text` staying the
/// only required field, so a surface that ignores `kind`/`step`/`detail` renders exactly
/// what it always did — and the cheapest way to break that is to start stamping `kind`
/// on everything for tidiness.
/// </para>
///
/// <para>
/// Deterministic and offline: a real Trace with a capturing sink, and the real
/// SafetyFilter blocking a real call.
/// </para>
/// </summary>
public static async Task<int> VerifyThinkingStepsAsync(IServiceProvider provider)
{
    var failures = new List<string>();
    var captured = new List<AgentEvent>();
    var trace = new Trace(
        provider.GetRequiredService<ILoggerFactory>().CreateLogger<Trace>(),
        ev => { captured.Add(ev); return Task.CompletedTask; });

    using (trace.BeginGoalScope("goal-steps", "c"))
    {
        await trace.ThinkingStepAsync("Composing the plan", "7 tasks · 20 tools");
        await trace.ThinkingStepAsync("Drafted 7 steps");
        await trace.ThinkingStepAsync("   ", "a step with no headline is not a step");
        await trace.ThinkingAsync("the model's own words");
    }

    JsonObject? Payload(int i) => i < captured.Count ? captured[i].Payload : null;

    // 1. A STEP IS WHOLE. Headline, sub-line, and a `text` that reads as a sentence for
    //    anything that only knows about `text`.
    var step = Payload(0);
    if (step?["kind"]?.GetValue<string>() != ThinkingKinds.Step
        || step["step"]?.GetValue<string>() != "Composing the plan"
        || step["detail"]?.GetValue<string>() != "7 tasks · 20 tools"
        || step["text"]?.GetValue<string>() != "Composing the plan — 7 tasks · 20 tools")
    {
        failures.Add($"a step must carry kind/step/detail and a joined text, got {step?.ToJsonString()}");
    }

    // 2. No detail means NO detail key — not an empty string a client has to test for.
    var bare = Payload(1);
    if (bare is null || bare.ContainsKey("detail") || bare["text"]?.GetValue<string>() != "Drafted 7 steps")
    {
        failures.Add($"a step with no sub-line must omit `detail` entirely, got {bare?.ToJsonString()}");
    }

    // 3. A step with no headline is dropped rather than emitted blank.
    if (captured.Count != 3)
    {
        failures.Add($"a whitespace-only step must not be emitted, got {captured.Count} events");
    }

    // 4. BACK-COMPAT, and this is the one worth having. Narration carries `text` and
    //    NOTHING else, so a v6 client sees the exact bytes it saw before v7.
    var narration = captured.Count >= 3 ? captured[2].Payload : null;
    if (narration is null || narration.Count != 1 || narration["text"]?.GetValue<string>() != "the model's own words")
    {
        failures.Add($"narration must stay exactly {{text}} — a v6 client must not see new keys, got {narration?.ToJsonString()}");
    }

    // 5. A SAFETY BLOCK SAYS SO, in the run's own transcript. Until v7 the most
    //    interesting thing this engine ever does reached the user as a chip summary and
    //    a number on the plan card, and never as a sentence at the moment it happened.
    var filter = provider.GetRequiredService<SafetyFilter>();
    var armed = provider.GetRequiredService<ArmedPolicies>();
    captured.Clear();
    filter.SetTrace(trace);
    var hard = new JsonObject { ["allergens"] = new JsonArray("peanuts") };
    // A kernel with one plugin and the real filter — the Kernel itself is built by
    // GoalAgent rather than registered, and this gate needs a real invocation to travel
    // the real filter rather than a hand-called method.
    var builder = Kernel.CreateBuilder();
    builder.Plugins.AddFromObject(provider.GetRequiredService<ShoppingListPlugin>(), "ShoppingList");
    var kernel = builder.Build();
    kernel.FunctionInvocationFilters.Add(filter);

    using (trace.BeginGoalScope("goal-block", "c"))
    using (armed.Arm("goal-block", hard, (JsonObject)hard.DeepClone()))
    {
        var fn = kernel.Plugins.GetFunction("ShoppingList", "Add");
        await kernel.InvokeAsync(fn, new KernelArguments { ["items"] = new[] { "peanut butter" }, ["reason"] = "test" });
    }

    var notice = captured.FirstOrDefault(e =>
        e.Event == AgentEventKinds.Thinking
        && e.Payload["kind"]?.GetValue<string>() == ThinkingKinds.Notice);
    if (notice is null)
    {
        failures.Add("a safety block must be said out loud as a notice step, not only counted");
    }
    else if (!(notice.Payload["detail"]?.GetValue<string>() ?? "").Contains("peanut", StringComparison.OrdinalIgnoreCase))
    {
        failures.Add($"the notice must carry the reason it blocked, got {notice.Payload["detail"]}");
    }

    // 6. COUNTED VERDICTS. "7 steps" alone says the engine ran; the rest says what it
    //    weighed, which is the difference between an animation and a report.
    foreach (var (steps, considered, rejected, want) in new (int, int?, int, string)[]
    {
        (7, 17, 5, "7 steps · 17 considered, 5 rejected"),
        (7, 17, 0, "7 steps · 17 considered"),
        (7, null, 5, "7 steps · 5 rejected"),
        (7, null, 0, "7 steps"),
    })
    {
        var got = GoalAgent.PlannerVerdict(steps, considered, rejected);
        if (got != want) failures.Add($"planner verdict: expected \"{want}\", got \"{got}\"");
    }

    foreach (var f in failures) Console.WriteLine($"  FAIL {f}");
    Console.WriteLine("gate 23 (thinking steps: whole, and v6-compatible): " + (failures.Count == 0 ? "PASS" : $"FAIL: {failures.Count}"));
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// v7-M2 GATE: the day tick TELLS the family everything and ASKS about one thing.
///
/// <para>
/// Act 2 shows two changes overnight — a hard training day and a fish delivery — and
/// exactly one approval. That is a real distinction in the harness, not staging: a
/// non-material change is listed in the day summary and never opens an approval, and
/// before v7 it went nowhere at all (only the single material change reached
/// <c>DayAdvanced.Events</c>, so an observed non-material change was indistinguishable
/// from a quiet day).
/// </para>
///
/// <para>
/// Deterministic and offline: the real observer, the real feed, the real clock. No LLM,
/// so the ADAPTATION itself is out of scope here — what is in scope is that exactly one
/// change is eligible to cause one, and that its steer carries both facts, which is the
/// only reason a single adaptation can explain both.
/// </para>
/// </summary>
public static async Task<int> VerifyDayTickAsync(IServiceProvider provider)
{
    var failures = new List<string>();
    var observer = provider.GetServices<IDomainObserver>().First(o => o.Domain == "meal_plan");
    var workout = provider.GetRequiredService<WorkoutPlugin>();
    var clock = provider.GetRequiredService<IClock>();

    // 1. THE WORLD FILE SEEDS AND READS. A missing data file fails silently — the
    //    observer catches FileNotFoundException, returns no changes, and the goal just
    //    never adapts — so a gate that only checked behaviour would pass on an empty world.
    var routine = JsonNode.Parse(await workout.GetWeeklyRoutine())?.AsObject();
    if (routine?["target_steps_per_day"]?.GetValue<int>() is not 8000)
    {
        failures.Add($"Workout.GetWeeklyRoutine must return the household's step target, got {routine?.ToJsonString()}");
    }
    if ((routine?["week"]?.AsArray()?.Count ?? 0) != 7)
    {
        failures.Add("the routine must cover a full week, or 'workout-friendly' has nothing to shape a week against");
    }

    // 2. THE GENERIC-CLOCK RULE holds for activity too: -1 is yesterday on any day the
    //    demo runs, resolved at READ time. An absolute date here would go stale between
    //    demos in a way nobody notices until the numbers read as a week old.
    var recent = JsonNode.Parse(await workout.GetRecentActivity())?.AsArray();
    var yesterday = recent?
        .Select(n => n?.AsObject())
        .OfType<JsonObject>()
        .FirstOrDefault(d => d["day_offset"]?.GetValue<int>() == -1);
    var wantYesterday = clock.Today.AddDays(-1).ToString("yyyy-MM-dd");
    if (yesterday?["date"]?.GetValue<string>() != wantYesterday)
    {
        failures.Add($"recent activity must resolve day_offset -1 to {wantYesterday}, got {yesterday?["date"]}");
    }

    // 3. THE SPIKE IS NOT IN THE STEADY STATE. If 12,400 steps were seeded here it
    //    would already be true on day 1, the plan would have been built knowing it, and
    //    Act 2's adaptation would be reacting to nothing new. It belongs in the feed.
    if ((recent?.ToJsonString() ?? "").Contains("12400", StringComparison.Ordinal))
    {
        failures.Add("the Act 2 activity spike must live in daily_events.json, not in workout.json's steady state");
    }

    // 4. DAY 1 OF THE FEED: two changes, one of them material.
    var goal = new GoalRecord
    {
        Dispatch = new Dispatch
        {
            GoalId = "goal-week",
            CorrelationId = "c",
            Domain = "meal_plan",
            Objective = "plan my weekly meal",
            Constraints = new TaskConstraints { Hard = new JsonObject() },
            TimeWindow = new TimeWindow
            {
                Start = clock.Today.ToString("yyyy-MM-dd"),
                End = clock.Today.AddDays(6).ToString("yyyy-MM-dd")
            }
        },
        Tasks = [],
        Plan = Enumerable.Range(1, 7)
            .Select(day => new PlanItem { Id = $"d{day}", Day = day, Title = $"Dinner {day}" })
            .ToArray(),
        WorldSnapshot = await observer.CaptureAsync()
    };

    // The snapshot froze the feed's fire dates; now advance INTO day 2, which is what
    // pressing Advance day does once.
    if (clock is SimulatedClock sim) sim.AdvanceDay();

    var changes = await observer.ObserveAsync(goal);
    if (changes.Count != 2)
    {
        failures.Add($"day 1 must surface exactly two changes (a training day and a delivery), got {changes.Count}: "
                     + string.Join(", ", changes.Select(c => c.Key)));
    }

    var material = changes.Where(c => c.Material).ToArray();
    if (material.Length != 1 || material[0].Kind != "inventory.restocked")
    {
        failures.Add("exactly one day-1 change may be material, and it is the delivery — got "
                     + string.Join(", ", material.Select(c => c.Kind)));
    }

    var note = changes.FirstOrDefault(c => c.Kind == "workout.activity_logged");
    if (note is null)
    {
        failures.Add("the training day must be observed at all — it is half of what Advance day reports");
    }
    else
    {
        if (note.Material)
        {
            failures.Add("a workout is worth TELLING the family about and not worth asking them to approve a plan change for");
        }
        if (!string.IsNullOrWhiteSpace(note.Steer))
        {
            failures.Add("an informational change must carry no steer, or it becomes a re-plan by accident");
        }
        if (string.IsNullOrWhiteSpace(note.Description))
        {
            failures.Add("an informational change still needs a description — it is what the day summary shows");
        }
    }

    // 5. ONE ADAPTATION, BOTH REASONS. This is why two events are allowed to produce
    //    one approval: the material change's steer quotes the other one's numbers, so
    //    the single re-plan the user approves can explain itself in full.
    var steer = material.FirstOrDefault()?.Steer ?? "";
    foreach (var mustSay in new[] { "fish", "12,400" })
    {
        if (!steer.Contains(mustSay, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"the delivery's steer must name '{mustSay}' — one adaptation has to account for both changes");
        }
    }

    // 6. EXACTLY ONCE, both kinds. The tick dedups through one "already surfaced" set;
    //    a listed change that re-fires every tick is a day summary that repeats itself
    //    forever, which reads as a broken agent rather than a quiet week.
    foreach (var change in changes) goal.EmittedMaterialChanges.Add(change.Key);
    var again = (await observer.ObserveAsync(goal))
        .Where(c => !goal.EmittedMaterialChanges.Contains(c.Key))
        .ToArray();
    if (again.Length != 0)
    {
        failures.Add($"a surfaced change must not surface again, got {string.Join(", ", again.Select(c => c.Key))}");
    }

    foreach (var f in failures) Console.WriteLine($"  FAIL {f}");
    Console.WriteLine("gate 22 (day tick: two changes, one approval): " + (failures.Count == 0 ? "PASS" : $"FAIL: {failures.Count}"));
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// --verify-away-immune (v7 gate 26) — A DAY THE HOUSEHOLD EMPTIED STAYS EMPTY.
///
/// <para>
/// The bug this exists for: the family approved "we're away Sunday and Monday", the meal
/// week marked both days skipped, and the next Advance day fired "the paneer spoiled"
/// against Sunday — whose steer says "change tonight's dinner" — so the model cooked
/// dinner on a day nobody is home. The cross-goal moment is the demo's headline, and the
/// very next tick quietly undid half of it.
/// </para>
///
/// <para>
/// Two independent layers, because one is a judgement and the other is a guarantee: the
/// observer stops RAISING the change as material (so no LLM call, no approval), and
/// <c>DropSkippedRows</c> stops any patch from LANDING on a skipped row whatever raised
/// it. The gate asserts both, plus the thing that makes it humane — the change is still
/// TOLD. A family that is away still wants to know their fridge lost something.
/// </para>
/// </summary>
public static async Task<int> VerifyAwayImmuneAsync(IServiceProvider provider)
{
    var failures = new List<string>();
    var observer = provider.GetServices<IDomainObserver>().First(o => o.Domain == "meal_plan");
    var clock = provider.GetRequiredService<IClock>();

    // A week whose Day 3 is away — exactly the shape the cross-goal moment leaves behind.
    // day2-shortage in the feed targets Day 3, so this is the collision, not a contrivance.
    PlanItem[] Week() => Enumerable.Range(1, 7)
        .Select(day => new PlanItem
        {
            Id = $"d{day}",
            Day = day,
            Title = day == 3 ? "Away — no meal planned" : $"Dinner {day}",
            Status = day == 3 ? PlanItemStatuses.Skipped : PlanItemStatuses.Planned,
            StatusReason = day == 3 ? "you're away · from Vacation Home Prep" : null
        })
        .ToArray();

    var goal = new GoalRecord
    {
        Dispatch = new Dispatch
        {
            GoalId = "goal-week",
            CorrelationId = "c",
            Domain = "meal_plan",
            Objective = "plan my weekly meal",
            Constraints = new TaskConstraints { Hard = new JsonObject() },
            TimeWindow = new TimeWindow
            {
                Start = clock.Today.ToString("yyyy-MM-dd"),
                End = clock.Today.AddDays(6).ToString("yyyy-MM-dd")
            }
        },
        Tasks = [],
        Plan = Week(),
        WorldSnapshot = await observer.CaptureAsync()
    };

    // Advance INTO day 3 — two presses of Advance day, which is where the demo is when
    // the trip starts.
    if (clock is SimulatedClock sim) { sim.AdvanceDay(); sim.AdvanceDay(); }

    var changes = await observer.ObserveAsync(goal);
    var shortage = changes.FirstOrDefault(c => c.Kind == "inventory.shortage");
    if (shortage is null)
    {
        failures.Add("the day-3 shortage must still be OBSERVED — the fix is to stop it re-planning, not to hide it");
    }
    else
    {
        if (shortage.Material)
        {
            failures.Add("a change aimed at an away day must not be material — there is no dinner that day to change");
        }
        if (!shortage.Description.Contains("away", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"the day summary has to say WHY nothing changed, got: {shortage.Description}");
        }
    }

    // The other half: whatever raises a change, a patch may not land on a skipped row.
    // Asserted directly rather than through an LLM, because a guarantee that only holds
    // when the model cooperates is not a guarantee.
    var worldChange = new WorldChange
    {
        Key = "k", Kind = "inventory.shortage", Description = "d",
        AffectedPlanItems = ["d3"], Material = true
    };
    var attempted = new PlanItem[]
    {
        new() { Id = "d3", Day = 3, Title = "Paneer-free Rajma" },
        new() { Id = "d5", Day = 5, Title = "Something else" }
    };
    var kept = GoalAgent.DropSkippedRows(Week(), worldChange, attempted);
    if (kept.Any(r => r.Id == "d3"))
    {
        failures.Add("a patch upsert aimed at a skipped row must be dropped, whatever produced it");
    }
    if (!kept.Any(r => r.Id == "d5"))
    {
        failures.Add("dropping the away row must not drop the rest of the patch with it");
    }

    // ...EXCEPT the change that writes them. If a constraint change could not touch a
    // skipped row, a cancelled trip could never give the family their week back.
    var constraintChange = worldChange with { Kind = "constraints.changed" };
    if (GoalAgent.DropSkippedRows(Week(), constraintChange, attempted).Count != 2)
    {
        failures.Add("a constraints.changed patch MAY write skipped rows — it is the path that sets and clears them");
    }

    foreach (var f in failures) Console.WriteLine($"  FAIL {f}");
    Console.WriteLine("gate 26 (an away day stays away): " + (failures.Count == 0 ? "PASS" : $"FAIL: {failures.Count}"));
    return failures.Count == 0 ? 0 : 1;
}

public static async Task<int> VerifyEnvelopeAsync(IServiceProvider provider, string dataDir)
{
    var failures = new List<string>();
    var resolver = provider.GetRequiredService<IPolicyResolver>();
    var armed = provider.GetRequiredService<ArmedPolicies>();
    var safety = provider.GetRequiredService<SafetyFilter>();
    var store = provider.GetRequiredService<IProductApiAdapter>();
    var shopping = provider.GetRequiredService<ShoppingListPlugin>();
    var observer = provider.GetServices<IDomainObserver>().First(o => o.Domain == "grocery_cost");

    JsonObject Envelope(double cap) => new() { ["cap"] = cap, ["period"] = "monthly" };
    JsonObject Hard(double goalCap) => new() { ["budget_cap"] = goalCap, ["budget_envelope"] = Envelope(600.0) };
    async Task<double> SpentAsync() => (await store.LoadResolvedAsync("budget"))["spent"]?.GetValue<double>() ?? 0;

    var spent0 = await SpentAsync();

    // 1. The envelope narrows a cap that exceeds what is left, and leaves alone one
    //    that does not. A trip's $1500 cannot mean $1500 when the month holds $600.
    var trip = await resolver.ResolveAsync(Hard(1500.0));
    if (trip["budget_cap"]?.GetValue<double>() is not { } tripCap || Math.Abs(tripCap - (600.0 - spent0)) > 0.001)
    {
        failures.Add($"a $1500 trip cap must narrow to the ${600.0 - spent0:0.00} left in the envelope, got {trip["budget_cap"]}");
    }

    var week = await resolver.ResolveAsync(Hard(120.0));
    if (week["budget_cap"]?.GetValue<double>() is not 120.0)
    {
        failures.Add($"a $120 week fits inside the remaining envelope and must be left alone, got {week["budget_cap"]}");
    }

    // 2. No envelope on the dispatch: nothing to narrow against, cap untouched.
    var noEnvelope = await resolver.ResolveAsync(new JsonObject { ["budget_cap"] = 120.0 });
    if (noEnvelope["budget_cap"]?.GetValue<double>() is not 120.0)
    {
        failures.Add($"without an envelope the dispatched cap stands, got {noEnvelope["budget_cap"]}");
    }

    // 3. THE CROSS-GOAL EFFECT. Arm a grocery goal, let a DIFFERENT goal place an
    //    order, then re-resolve: the grocery goal's ceiling must have moved, without
    //    anyone touching the grocery goal.
    using (armed.Arm("goal-grocery", Hard(120.0), await resolver.ResolveAsync(Hard(120.0))))
    {
        var before = armed.ActiveHard()?["budget_cap"]?.GetValue<double>() ?? -1;

        using (armed.Arm("goal-party", Hard(200.0)))
        {
            await shopping.PlaceOrder(500.0);
        }

        var spentAfter = await SpentAsync();
        if (Math.Abs(spentAfter - (spent0 + 500.0)) > 0.001)
        {
            failures.Add($"an approved order must consume the household budget: spent {spent0} -> {spentAfter}, expected {spent0 + 500.0}");
        }

        await safety.ReResolveAsync("goal-grocery");
        var after = armed.ActiveHard()?["budget_cap"]?.GetValue<double>() ?? -1;
        var expected = Math.Max(0, Math.Round(600.0 - spentAfter, 2));
        if (Math.Abs(after - expected) > 0.001)
        {
            failures.Add($"the grocery goal's ceiling must fall to the ${expected:0.00} left after another goal spent, got {after}");
        }

        if (after >= before)
        {
            failures.Add($"another goal's approved order must SHRINK this goal's headroom: {before} -> {after}");
        }

        // Re-resolving twice must not move it again (narrowing is idempotent) …
        await safety.ReResolveAsync("goal-grocery");
        if (Math.Abs((armed.ActiveHard()?["budget_cap"]?.GetValue<double>() ?? -1) - after) > 0.001)
        {
            failures.Add("re-resolving twice moved the ceiling again");
        }

        // … and — the row that actually catches resolving from the wrong block — the
        // ceiling must be able to CLIMB BACK. Narrowing is a min(), so re-narrowing an
        // already-narrowed cap looks harmless; it only shows up when the envelope frees
        // up (a refund, a new billing period) and a ceiling computed from the last
        // effective value can never recover.
        var refunded = await store.LoadResolvedAsync("budget");
        refunded["spent"] = spent0;
        await store.SaveAsync("budget", refunded);
        await safety.ReResolveAsync("goal-grocery");
        var recovered = armed.ActiveHard()?["budget_cap"]?.GetValue<double>() ?? -1;
        if (Math.Abs(recovered - 120.0) > 0.001)
        {
            failures.Add($"once the envelope frees up the ceiling must return to the dispatched $120, got {recovered}");
        }

        // Put the spend back so the observer rows below see the squeezed world.
        refunded["spent"] = spentAfter;
        await store.SaveAsync("budget", refunded);

        // 4. AND THE OTHER GOAL NOTICES. A ceiling that quietly shrinks is a plan that
        //    fails at approval time; the point is that the goal re-plans first.
        var goal = new GoalRecord
        {
            Dispatch = new Dispatch
            {
                GoalId = "goal-grocery",
                CorrelationId = "c",
                Domain = "grocery_cost",
                Objective = "keep the kitchen stocked for less",
                Constraints = new TaskConstraints { Hard = Hard(120.0) },
                TimeWindow = new TimeWindow { Start = "2026-07-29", End = "2026-08-05" }
            },
            Tasks = [],
            WorldSnapshot = new JsonObject { ["budget"] = new JsonObject { ["spent"] = spent0 } }
        };

        var changes = await observer.ObserveAsync(goal);
        var squeeze = changes.FirstOrDefault(c => c.Kind == "budget.envelope_squeezed");
        if (squeeze is null)
        {
            failures.Add("the grocery goal must notice that another goal spent the shared envelope");
        }
        else if (!squeeze.Material || string.IsNullOrWhiteSpace(squeeze.Steer))
        {
            failures.Add("the squeeze must be material and carry a steer, or nothing re-plans");
        }

        // 5. NOT NOISE. Before the envelope is squeezed past this goal's own cap,
        //    another goal's spending is not this goal's problem — an agent that
        //    interrupts a family over $2 of someone else's shopping gets switched off.
        var quiet = new GoalRecord
        {
            Dispatch = goal.Dispatch,
            Tasks = [],
            WorldSnapshot = new JsonObject { ["budget"] = new JsonObject { ["spent"] = await SpentAsync() } }
        };
        if ((await observer.ObserveAsync(quiet)).Any(c => c.Kind == "budget.envelope_squeezed"))
        {
            failures.Add("no new spending since the plan means no squeeze to report");
        }
    }

    foreach (var failure in failures) Console.Error.WriteLine($"  FAIL {failure}");
    Console.Out.WriteLine(failures.Count == 0
        ? "gate 20 (household envelope: two goals, one wallet): PASS"
        : $"gate 20 FAIL: {failures.Count}");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// v6-M2 GATE: the budget cap the planner is TOLD about is the goal's armed cap —
/// the same number the SafetyFilter will enforce — and the device no longer keeps a
/// copy of its own.
///
/// <para>
/// The bug this guards is not a crash. Until v6, data/budget.json carried
/// <c>cap: 120.0</c> beside the cloud's <c>constraints.hard.budget_cap</c>: one
/// policy, two copies, hand-synced. The planner read the device's copy, the filter
/// enforced the cloud's, and nothing compared them — so a party goal capped at $200
/// by the account was planned against $120, and a trip capped at $1500 was planned
/// against $120 as well. Every plan still came back. It was just planned against the
/// wrong household.
/// </para>
///
/// <para>
/// Deterministic and offline: it arms policies directly and calls the plugin, no LLM
/// and no kernel in the loop.
/// </para>
/// </summary>
public static async Task<int> VerifyActivePolicyAsync(ArmedPolicies armed, BudgetPlugin budget, string dataDir)
{
    var failures = new List<string>();

    async Task<JsonObject> StatusAsync()
        => JsonNode.Parse(await budget.GetBudgetStatus())?.AsObject() ?? new JsonObject();

    // 1. The de-dup itself. A `cap` back in the world file is the regression: it
    //    would read like the authority while enforcing nothing.
    var budgetFile = Path.Combine(dataDir, "budget.json");
    if (File.Exists(budgetFile)
        && JsonNode.Parse(File.ReadAllText(budgetFile))?.AsObject() is { } world
        && world["cap"] is not null)
    {
        failures.Add($"{budgetFile} carries its own 'cap' again — policy belongs to the account, not the device");
    }

    // 2. No goal scope: no cap. Reporting 0 would tell the planner it is already
    //    over budget on a goal that simply has no ceiling.
    var unscoped = await StatusAsync();
    if (unscoped["cap"] is not null)
    {
        failures.Add($"outside a goal scope there is no cap to report, got {unscoped["cap"]}");
    }

    if (unscoped["spent"]?.GetValue<double>() is not > 0)
    {
        failures.Add("spent is device world state and must still be reported");
    }

    // 3. Inside a goal, the cap is that goal's armed cap — and headroom is measured
    //    from it.
    var spent = unscoped["spent"]!.GetValue<double>();
    using (armed.Arm("goal-party", new JsonObject { ["budget_cap"] = 200.0 }))
    {
        var status = await StatusAsync();
        if (status["cap"]?.GetValue<double>() is not 200.0)
        {
            failures.Add($"a party goal must be planned against its armed $200 cap, got {status["cap"]}");
        }

        if (status["remaining"]?.GetValue<double>() is not { } remaining || Math.Abs(remaining - (200.0 - spent)) > 0.001)
        {
            failures.Add($"remaining must be the armed cap minus what is spent, got {status["remaining"]}");
        }
    }

    // 4. A DIFFERENT goal, a different cap — the number is per goal, not a global.
    using (armed.Arm("goal-trip", new JsonObject { ["budget_cap"] = 1500.0 }))
    {
        var status = await StatusAsync();
        if (status["cap"]?.GetValue<double>() is not 1500.0)
        {
            failures.Add($"a trip goal must be planned against its armed $1500 cap, got {status["cap"]}");
        }
    }

    // 5. A goal with no cap at all (energy saving) reports none, rather than 0.
    using (armed.Arm("goal-energy", new JsonObject { ["quiet_hours"] = new JsonObject { ["start"] = "21:30", ["end"] = "07:00" } }))
    {
        var status = await StatusAsync();
        if (status["cap"] is not null)
        {
            failures.Add($"a goal with no budget_cap has no ceiling to report, got {status["cap"]}");
        }
    }

    foreach (var failure in failures) Console.Error.WriteLine($"  FAIL {failure}");
    Console.Out.WriteLine(failures.Count == 0
        ? "gate 19 (active policy: one cap, from the account): PASS"
        : $"gate 19 FAIL: {failures.Count}");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// M1 GATE: two goals with DIFFERENT hard constraints, running concurrently,
/// must each be checked against their own — and only their own.
///
/// <para>
/// This is the regression test for a live safety bug: the armed policy was one
/// field on a singleton filter, so goal B's dispatch overwrote goal A's
/// constraints mid-plan and the gate then enforced the wrong family's allergens.
/// The two goals here interleave deliberately (awaits inside both scopes, a
/// barrier between arming and checking) so that a shared field cannot pass:
/// whichever armed last would win both assertions.
/// </para>
///
/// <para>Deterministic and offline — it drives <c>SafetyFilter.CheckCurrent</c>,
/// the same scope lookup the kernel pipeline uses, with no LLM involved.</para>
/// </summary>
public static async Task<int> VerifyPolicyIsolationAsync(SafetyFilter safety)
{
    // Goal A cannot have peanuts; goal B cannot have dairy (which the policy
    // expands to milk/yogurt/paneer/cheese).
    var goalA = new JsonObject { ["allergens"] = new JsonArray("peanuts") };
    var goalB = new JsonObject { ["allergens"] = new JsonArray(), ["dietary"] = new JsonArray("dairy") };

    var armed = new TaskCompletionSource();
    var failures = new List<string>();

    async Task RunGoal(string goalId, JsonObject hard, string mustBlock, string mustAllow)
    {
        using var scope = safety.BeginGoal(goalId, hard);

        // Both goals are now armed before either checks — a shared field would
        // hold only the second one's constraints from here on.
        if (goalId == "goal-a") { armed.SetResult(); }
        await armed.Task;
        await Task.Yield();

        var blocked = safety.CheckCurrent("ShoppingList", "Add", new KernelArguments { ["items"] = new[] { mustBlock } });
        if (blocked is null)
        {
            failures.Add($"{goalId}: '{mustBlock}' should have been BLOCKED by its own constraints, but passed");
        }

        var allowed = safety.CheckCurrent("ShoppingList", "Add", new KernelArguments { ["items"] = new[] { mustAllow } });
        if (allowed is not null)
        {
            failures.Add($"{goalId}: '{mustAllow}' should have been ALLOWED ({goalId} has no such constraint), but was blocked: {allowed}");
        }
    }

    // NB: terms are matched as literal substrings, so the probes use terms the
    // current checker actually recognises ("peanuts", not "peanut butter").
    // This gate is about ISOLATION between goals, not about match quality.
    await Task.WhenAll(
        Task.Run(() => RunGoal("goal-a", goalA, mustBlock: "peanuts", mustAllow: "milk")),
        Task.Run(() => RunGoal("goal-b", goalB, mustBlock: "milk", mustAllow: "peanuts")));

    // Each goal's verdict must be its own, after the fact too.
    if (safety.GateFor("goal-a") != SafetyGates.Passed) failures.Add("goal-a gate should be 'passed' (CheckCurrent records nothing)");
    if (safety.GateFor("unknown-goal") != SafetyGates.Passed) failures.Add("an unknown goal should report a clean gate, not throw");

    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"  FAIL {failure}");
    }

    Console.Out.WriteLine(failures.Count == 0
        ? "gate 5 (per-goal policy isolation): PASS"
        : $"gate 5 FAIL: {failures.Count} assertion(s) — two goals are seeing each other's safety policy");
    return failures.Count == 0 ? 0 : 1;
}

/// <summary>
/// Ensure a mock-world dir has its seed JSONs. Running a SECOND agent with its own
/// <c>--data ./data-b</c> (so two instances don't clobber each other's world) would
/// otherwise die on a missing <c>calendar.json</c>; seed it from the repo's
/// <c>./data</c> on first use. Only ever seeds a dir with NO <c>*.json</c> — an
/// already-populated world is never overwritten. Mirrors the Tizen agent, which
/// seeds a writable copy out of its read-only bundle.
/// </summary>
public static void EnsureDataDir(string dataDir)
{
    const string seed = "data";
    try
    {
        if (Path.GetFullPath(dataDir) == Path.GetFullPath(seed))
        {
            return; // this IS the seed
        }
        if (!Directory.Exists(seed))
        {
            return; // nothing to seed from — let the store fail loudly
        }
        Directory.CreateDirectory(dataDir);

        // v7 — FILL IN WHAT IS MISSING, rather than skipping a dir that has any json at
        // all. The old rule ("already has a world → leave it alone") had a failure mode
        // with no symptom: add a world file and every pre-existing --data dir silently
        // lacks it, so the observer that reads it throws FileNotFoundException, returns
        // no changes, and the goal simply never adapts. Nothing errors. It cost real time
        // twice — once for workout.json, once for deliveries.json — and on a Hub that has
        // already run an older build there is no `rm -rf` to reach for.
        //
        // Copying per-file is safe because overwrite stays FALSE: a file the run has
        // mutated is never clobbered, so this only ever adds what was never there.
        var added = 0;
        foreach (var file in Directory.EnumerateFiles(seed, "*.json"))
        {
            var target = Path.Combine(dataDir, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target, overwrite: false);
            added++;
        }
        if (added > 0)
        {
            Console.Error.WriteLine($"seeded mock world: {dataDir} <- {seed} ({added} file(s))");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"could not seed {dataDir}: {ex.Message}");
    }
}

/// <summary>
/// Resolve the device_id (the cloud's pairing key): an explicit <c>--device-id</c>
/// or <c>$DEVICE_ID</c> wins; otherwise a stable UUID persisted in the data dir
/// (<c>&lt;data&gt;/device_id</c>) — generated once on first run. Plain File I/O,
/// so the SAME scheme works on Ubuntu and (later) on the Tizen Hub.
/// </summary>
public static string ResolveDeviceId(string? cliValue, string dataDir)
{
    var configured = cliValue ?? Environment.GetEnvironmentVariable("DEVICE_ID");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured.Trim();
    }

    var path = Path.Combine(dataDir, "device_id");
    try
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }
        // Do NOT create the dir here — an empty half-made data dir masks a bad
        // --data path as a confusing "missing calendar.json" later. EnsureDataDir
        // owns creating/seeding it.
        if (!Directory.Exists(dataDir))
        {
            return Guid.NewGuid().ToString("N");
        }
        var generated = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, generated);
        return generated;
    }
    catch
    {
        // Non-persistent fallback (e.g. read-only data dir): still unique per run.
        return Guid.NewGuid().ToString("N");
    }
}

/// <summary>
/// A human label for the UI's device picker: <c>--device-name</c> / <c>$DEVICE_NAME</c>,
/// else <c>user@machine (shortid)</c>.
///
/// The default must be BOTH recognisable and UNIQUE — a picker of two identical labels
/// is useless. <c>user@machine</c> alone is not enough: two developers on identical VM
/// images are both <c>ubuntu@ubuntu</c> (and on a Tizen Hub every unit reports the same
/// user/host). So the short id — derived from the UNIQUE device_id — is always appended,
/// which makes the label unique by construction on any platform.
/// </summary>
public static string ResolveDeviceName(string? cliValue, string deviceId)
{
    var configured = cliValue ?? Environment.GetEnvironmentVariable("DEVICE_NAME");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured.Trim();
    }
    return $"{HostLabel()} ({ShortDeviceId(deviceId)})";
}

/// <summary>The first 6 chars of the device_id — enough to disambiguate a picker.</summary>
public static string ShortDeviceId(string deviceId)
    => deviceId.Length <= 6 ? deviceId : deviceId[..6];

private static string HostLabel()
{
    try
    {
        var user = Environment.UserName;
        var machine = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(machine))
        {
            return $"{user}@{machine}";
        }
    }
    catch
    {
        // fall through
    }
    return "GoalFlow Hub";
}

public static async Task RunSustainSimulationAsync(CliOptions options, GoalAgent agent, IClock clock)
{
    var contractPath = options.ContractPath ?? Path.Combine(options.DataDir, options.SimulateGuest ? "sample-contract-guest.json" : "sample-contract.json");
    var dispatch = LoadDispatch(contractPath, clock);
    var plan = await agent.RunAsync(dispatch);
    Console.Out.WriteLine(ContractJson.Serialize(plan));

    var days = options.SimulateGuest ? 2 : 5;
    for (var i = 0; i < days; i++)
    {
        var (status, proposal) = await agent.HandleControlAsync(new Control
        {
            GoalId = dispatch.GoalId,
            Command = ControlCommands.AdvanceDay
        });
        Console.Out.WriteLine(ContractJson.Serialize(status));
        if (proposal is null)
        {
            continue;
        }

        Console.Out.WriteLine(ContractJson.Serialize(proposal));
        var approval = new Approval
        {
            GoalId = dispatch.GoalId,
            CorrelationId = dispatch.CorrelationId,
            Payload = new ApprovalPayload
            {
                Decisions = [new ApprovalDecision { ProposalId = proposal.Payload.ProposalId, Approved = true }]
            }
        };
        Console.Out.WriteLine(ContractJson.Serialize(await agent.ApplyApprovalAsync(approval)));
        Console.Out.WriteLine(ContractJson.Serialize(await agent.ApplyApprovalAsync(approval)));
    }
}

public static string CopyDataToTemp(string dataDir)
{
    var source = Path.GetFullPath(dataDir);
    var target = Path.Combine(Path.GetTempPath(), $"goalflow-device-data-{Guid.NewGuid():N}");
    Directory.CreateDirectory(target);
    foreach (var file in Directory.EnumerateFiles(source, "*.json"))
    {
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }

    return target;
}

public static Dispatch LoadDispatch(string path, IClock clock)
{
    var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidOperationException($"{path} is not a JSON object.");
    ResolveTodayTokens(node, clock);
    return ContractJson.Deserialize<Dispatch>(node.ToJsonString(ContractJson.Options));
}

internal sealed class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);
    public void Dispose() { }
}

internal sealed class StderrLogger : ILogger
{
    private readonly string _category;
    public StderrLogger(string category) => _category = category;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {logLevel} {_category}: {formatter(state, exception)}");
        if (exception is not null)
        {
            Console.Error.WriteLine(exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// The contract used by the standalone <c>--goal --domain</c> CLI path (no cloud).
///
/// <para>
/// This MUST vary by domain. It used to hardcode a meal contract — "weekday dinners
/// planned", scope <c>{meal: dinner, days: Mon..Fri}</c> — for EVERY domain, so asking
/// for a vacation goal handed the planner a meal contract wearing a vacation label, and
/// the planner faithfully produced a week of dinners. The success criteria and scope are
/// the strongest shape signal the planner gets; a wrong one beats any prompt wording.
/// The meal_plan branch is kept byte-identical to the original so the gates don't move.
/// </para>
/// </summary>
public static Dispatch BuildLocalDispatch(string goal, string domain, IClock clock)
{
    var start = clock.Today.AddDays(1).ToString("yyyy-MM-dd");
    var end = clock.Today.AddDays(5).ToString("yyyy-MM-dd");

    var (criteria, scope, prefer) = domain switch
    {
        "guest_dinner" => (
            new[] { "menu honours every guest's dietary constraints", "prep timeline scheduled", "shopping proposals tiered" },
            new JsonObject { ["meal"] = "dinner", ["hosting"] = true },
            new JsonArray("more_vegetables")),
        "vacation_prep" => (
            new[] { "perishables used or frozen before departure", "appliances set to eco or off", "house locked and security armed" },
            new JsonObject { ["trip"] = "away_from_home", ["covers"] = new JsonArray("food", "appliances", "security", "deliveries") },
            new JsonArray("less_waste")),
        "birthday_party" => (
            new[] { "guests invited and headcount known", "cake and supplies within budget", "day-of schedule set" },
            new JsonObject { ["event"] = "birthday_party", ["covers"] = new JsonArray("guests", "cake", "supplies", "schedule") },
            new JsonArray("stay_under_budget")),
        "grocery_cost" => (
            new[] { "kitchen restocked to threshold", "basket priced under the budget cap", "cheaper substitutions taken where sensible" },
            new JsonObject { ["focus"] = "grocery_spend", ["covers"] = new JsonArray("stock", "offers", "prices") },
            new JsonArray("stay_under_budget", "less_waste")),
        "energy_saving" => (
            new[] { "heavy appliance runs shifted off-peak", "eco programs preferred", "standby waste cut" },
            new JsonObject { ["focus"] = "electricity_use", ["covers"] = new JsonArray("appliances", "tariff_windows", "standby") },
            new JsonArray("keep_comfort")),
        // meal_plan and anything unrecognised keep the original meal contract.
        _ => (
            new[] { "weekday dinners planned", "expiring inventory used", "shopping proposals tiered" },
            new JsonObject { ["meal"] = "dinner", ["days"] = new JsonArray("Mon", "Tue", "Wed", "Thu", "Fri") },
            new JsonArray("more_vegetables", "more_protein")),
    };

    return new Dispatch
    {
        GoalId = $"local-{Guid.NewGuid():N}",
        CorrelationId = $"local-{Guid.NewGuid():N}",
        Domain = domain,
        Objective = goal,
        SuccessCriteria = criteria,
        Constraints = new TaskConstraints
        {
            Hard = new JsonObject
            {
                ["allergens"] = new JsonArray(),
                ["dietary"] = new JsonArray(),
                ["medical"] = new JsonArray(),
                ["budget_cap"] = 60.0
            },
            Soft = new JsonObject { ["prefer"] = prefer }
        },
        Scope = scope,
        TimeWindow = new TimeWindow { Start = start, End = end },
        Context = new JsonObject { ["notes"] = "standalone CLI dispatch" }
    };
}

private static void ResolveTodayTokens(JsonNode? node, IClock clock)
{
    if (node is JsonObject obj)
    {
        foreach (var key in obj.Select(kv => kv.Key).ToArray())
        {
            if (obj[key] is JsonValue val && val.TryGetValue<string>(out var s))
            {
                obj[key] = ResolveTodayToken(s, clock);
            }
            else
            {
                ResolveTodayTokens(obj[key], clock);
            }
        }
    }
    else if (node is JsonArray arr)
    {
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonValue val && val.TryGetValue<string>(out var s))
            {
                arr[i] = ResolveTodayToken(s, clock);
            }
            else
            {
                ResolveTodayTokens(arr[i], clock);
            }
        }
    }
}

private static string ResolveTodayToken(string value, IClock clock)
{
    var match = Regex.Match(value, @"^\$\{today(?<sign>[+-])?(?<days>\d+)?\}$");
    if (!match.Success)
    {
        return value;
    }

    var days = match.Groups["days"].Success ? int.Parse(match.Groups["days"].Value) : 0;
    if (match.Groups["sign"].Value == "-") days = -days;
    return clock.Today.AddDays(days).ToString("yyyy-MM-dd");
}

public static LogLevel? ParseLogLevel()
    => Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("LOG_LEVEL"), ignoreCase: true, out var level) ? level : null;

/// <summary>Parses a positive integer config value; false (and 0) when absent/blank/non-positive.</summary>
public static bool TryParsePositiveInt(string? raw, out int value)
{
    if (int.TryParse(raw, out value) && value > 0)
    {
        return true;
    }
    value = 0;
    return false;
}

/// <summary>
/// --verify-deadline (M6 gate 15) — a stalled provider stream must not wedge a goal.
///
/// <para>
/// This reproduces the real failure rather than describing it: a local endpoint that
/// accepts the request, returns 200 with SSE headers, emits a few tokens, and then
/// goes silent forever. That is exactly what OpenRouter did twice in one session —
/// the device streamed, stopped mid-JSON, and sat there for FOUR HOURS while every
/// surface reported "Working out the steps…".
/// </para>
/// <para>
/// It is a real <c>IChatCompletionService</c> against a real socket, because the claim
/// under test is a claim about the SDK: that cancelling a linked token actually aborts
/// a streaming read. <c>HttpClient.Timeout</c> notably does NOT — streaming uses
/// <c>ResponseHeadersRead</c>, so the timeout is satisfied once headers arrive and the
/// body read is unbounded. Asserting on <see cref="CancellationTokenSource"/> in
/// isolation would prove the token cancels and tell us nothing about the hang.
/// </para>
/// </summary>
public static async Task<int> VerifyDeadlineAsync(ILoggerFactory loggerFactory)
{
    var log = loggerFactory.CreateLogger("verify-deadline");
    var failures = 0;
    void Check(bool ok, string what)
    {
        if (!ok) { failures++; Console.WriteLine($"  FAIL {what}"); }
        else { Console.WriteLine($"  ok   {what}"); }
    }

    using var listener = new System.Net.HttpListener();
    var port = 8100 + (Environment.ProcessId % 500);
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();

    var served = new TaskCompletionSource();
    var serve = Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        var body = context.Response.OutputStream;
        // A few WELL-FORMED chunks, so the client parses them happily and is committed
        // to the stream. The shape matters: an earlier version of this fixture omitted
        // id/object/created/model, the SDK threw JsonReaderException after 127ms, and
        // this gate PASSED — on a parse error, with the deadline never firing, because
        // JsonException is itself in the transient list. A fixture the client rejects
        // tests the rejection, not the hang.
        // Plain tokens: no quotes or braces. They were "{", "\"pl", "an\"" to look like
        // a plan being emitted, but an unescaped quote inside a JSON string value made
        // the chunk itself invalid ("content":""pl") — which the SDK rejected in 86ms,
        // and the loose assertions called a pass. What flows before the stall does not
        // matter; that it PARSES does.
        foreach (var token in new[] { "Planning", " the", " party" })
        {
            var chunk = System.Text.Encoding.UTF8.GetBytes(
                "data: {\"id\":\"chatcmpl-hang\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"hang-test\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\","
                + "\"content\":\"" + token + "\"},\"finish_reason\":null}]}\n\n");
            await body.WriteAsync(chunk);
            await body.FlushAsync();
        }
        served.SetResult();
        // ...and then nothing, ever. No error, no close: the exact shape of the hang.
        await Task.Delay(Timeout.Infinite);
    });

    var kernel = Kernel.CreateBuilder()
        .AddOpenAIChatCompletion(modelId: "hang-test", endpoint: new Uri($"http://127.0.0.1:{port}"), apiKey: "test")
        .Build();
    var chat = kernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
    var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
    history.AddUserMessage("plan something");

    // The goal's own token — NEVER cancelled here. That distinction is the whole
    // design: the deadline must be invisible to it, or a hang would be indistinguishable
    // from a shutdown and IsTransientProviderError would refuse to retry.
    using var goalCts = new CancellationTokenSource();
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(goalCts.Token);
    deadline.CancelAfter(TimeSpan.FromSeconds(3));

    var started = System.Diagnostics.Stopwatch.StartNew();
    Exception? thrown = null;
    try
    {
        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, null, kernel, deadline.Token))
        {
            // drain
        }
    }
    catch (Exception ex)
    {
        thrown = ex;
    }
    started.Stop();
    listener.Stop();

    await served.Task.WaitAsync(TimeSpan.FromSeconds(5));
    log.LogInformation("stream aborted after {Elapsed}ms with {Type}: {Message}",
        started.ElapsedMilliseconds, thrown?.GetType().Name ?? "<nothing>", thrown?.Message ?? "-");

    Check(thrown is not null, "a stalled stream throws instead of hanging forever");
    // The client must have PARSED the tokens and then WAITED. If it choked on the
    // fixture, everything below measures the choke and not the hang — which is exactly
    // how the first version of this gate passed.
    Check(thrown is OperationCanceledException,
        $"the DEADLINE ended the stream, not a parse error (got {thrown?.GetType().Name ?? "<nothing>"})");

    // Both bounds. The lower one is what makes this a test: anything that ends the
    // stream early (a malformed fixture, a refused connection) fails here, so the
    // assertion can only be satisfied by waiting out the deadline and no other way.
    Check(started.Elapsed >= TimeSpan.FromSeconds(2.5),
        $"it waited for the deadline rather than failing fast ({started.ElapsedMilliseconds}ms, deadline 3000ms)");
    Check(started.Elapsed < TimeSpan.FromSeconds(10),
        $"it gives up ON the deadline, not eventually ({started.ElapsedMilliseconds}ms)");

    Check(!goalCts.IsCancellationRequested, "the goal's own token is untouched — only the linked deadline fired");
    Check(thrown is not null && GoalAgent.IsTransientProviderErrorForTests(thrown, goalCts.Token),
        "a fired deadline classifies as TRANSIENT, so the existing retry handles it");

    // ...and the same exception under a REAL cancellation must NOT be retried, or
    // shutdown would spin through three attempts before giving up.
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Check(thrown is not null && !GoalAgent.IsTransientProviderErrorForTests(thrown, cancelled.Token),
        "the same exception under genuine cancellation is NOT transient");

    Console.WriteLine(failures == 0 ? "gate 15 (provider deadline): PASS" : $"gate 15 (provider deadline): FAIL: {failures}");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Gate 29 (v8-M1) — the two OpenRouter body fields SK does not model, and the promise that
/// they are INVISIBLE until someone asks for them.
///
/// <para>
/// HALF THIS GATE IS ABOUT THE FIELDS BEING ABSENT, and that half matters more. Every other
/// gate in <c>verify/</c> was written against a request body with no <c>provider</c> and no
/// <c>reasoning_effort</c> in it, and not one of them would notice if we started sending them —
/// they never reach the network. This one does, so "unset changes nothing" is a measured claim
/// rather than a hope.
/// </para>
///
/// <para>
/// It asserts on the WIRE, not on the settings object, because the claim under test is a claim
/// about SK's serializer: that <c>ExtraBody</c> lands as a top-level <c>provider</c> key and not
/// nested under <c>extra_body</c>, and that it does so on the streaming path too. Asserting on
/// <c>OpenAIPromptExecutionSettings</c> would prove only that we set a property.
/// </para>
/// </summary>
public static async Task<int> VerifyRequestShapeAsync(ILoggerFactory loggerFactory)
{
    var failures = 0;
    void Check(bool ok, string what)
    {
        if (!ok) { failures++; Console.WriteLine($"  FAIL {what}"); }
        else { Console.WriteLine($"  ok   {what}"); }
    }

    using var listener = new System.Net.HttpListener();
    var port = 8600 + (Environment.ProcessId % 400);
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();

    var bodies = new System.Collections.Concurrent.ConcurrentQueue<System.Text.Json.JsonElement>();
    using var stop = new CancellationTokenSource();
    var serve = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            System.Net.HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch { return; }

            string raw;
            using (var reader = new StreamReader(context.Request.InputStream))
            {
                raw = await reader.ReadToEndAsync();
            }

            var streaming = false;
            try
            {
                var parsed = System.Text.Json.JsonDocument.Parse(raw);
                bodies.Enqueue(parsed.RootElement.Clone());
                streaming = parsed.RootElement.TryGetProperty("stream", out var s)
                    && s.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            catch { /* an unparseable body fails the assertions below, which is the point */ }

            context.Response.StatusCode = 200;
            if (streaming)
            {
                // Mirror the real thing closely enough that the SDK commits to the stream:
                // the deadline gate learned the hard way that a fixture the client REJECTS
                // tests the rejection, not the behaviour under test.
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                var body = context.Response.OutputStream;
                var chunk = System.Text.Encoding.UTF8.GetBytes(
                    "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"shape-test\","
                    + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: [DONE]\n\n");
                await body.WriteAsync(chunk);
                await body.FlushAsync();
            }
            else
            {
                context.Response.ContentType = "application/json";
                var body = System.Text.Encoding.UTF8.GetBytes(
                    "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"shape-test\","
                    + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}");
                await context.Response.OutputStream.WriteAsync(body);
            }
            context.Response.Close();
        }
    });

    // Built PER ROUTING, not once: `provider` rides on the HttpClient now, so a kernel that was
    // built from a different routing would not be testing the thing under test.
    Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService ChatFor(LlmRouting routing)
        => Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(modelId: "shape-test", endpoint: new Uri($"http://127.0.0.1:{port}"),
                apiKey: "test", orgId: null, serviceId: null,
                httpClient: OpenRouterBodyHandler.CreateClient(routing))
            .Build()
            .GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();

    async Task<System.Text.Json.JsonElement> SendAsync(LlmRouting routing, LlmCallSite site, bool streaming)
    {
        while (bodies.TryDequeue(out _)) { }
        var chat = ChatFor(routing);
        var kernel = new Kernel();
        var settings = routing.Apply(new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIPromptExecutionSettings { Temperature = 0.1, MaxTokens = 64 }, site);
        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
        history.AddUserMessage("shape");
        try
        {
            if (streaming)
            {
                await foreach (var _ in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel)) { }
            }
            else
            {
                await Microsoft.SemanticKernel.ChatCompletion.ChatCompletionServiceExtensions.GetChatMessageContentAsync(chat, history, settings, kernel);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (send failed: {ex.GetType().Name}: {ex.Message})");
        }
        return bodies.TryDequeue(out var body) ? body : default;
    }

    static bool Has(System.Text.Json.JsonElement body, string key)
        => body.ValueKind == System.Text.Json.JsonValueKind.Object && body.TryGetProperty(key, out _);

    static string? Str(System.Text.Json.JsonElement body, string key)
        => body.ValueKind == System.Text.Json.JsonValueKind.Object && body.TryGetProperty(key, out var v)
           && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    // --- the no-op: this is the gate that protects every OTHER gate ---
    var none = LlmRouting.FromEnvironment(_ => null);
    Check(none.IsNoOp, "nothing set at all resolves to LlmRouting.None");

    var plain = await SendAsync(none, LlmCallSite.Compose, streaming: false);
    Check(plain.ValueKind == System.Text.Json.JsonValueKind.Object, "the fixture captured a request body");
    Check(!Has(plain, "provider"), "unset: a non-streaming body carries NO provider key");
    Check(!Has(plain, "reasoning_effort"), "unset: a non-streaming body carries NO reasoning_effort key");
    Check(!Has(plain, "extra_body"), "unset: nothing leaks through as a literal extra_body key");

    var plainStream = await SendAsync(none, LlmCallSite.Grounding, streaming: true);
    Check(!Has(plainStream, "provider") && !Has(plainStream, "reasoning_effort"),
        "unset: a STREAMING body carries neither");

    // --- routed ---
    var env = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["OPENROUTER_PROVIDER_ORDER"] = "cerebras,groq",
        ["LLM_REASONING_EFFORT"] = "medium",
        ["LLM_REASONING_EFFORT_COMPOSE"] = "off",
    };
    var routed = LlmRouting.FromEnvironment(k => env.TryGetValue(k, out var v) ? v : null);
    Check(!routed.IsNoOp, "with the vars set it is no longer a no-op");

    var ground = await SendAsync(routed, LlmCallSite.Grounding, streaming: true);
    Check(Has(ground, "provider"), "provider is a TOP-LEVEL body field on the STREAMING path");
    if (Has(ground, "provider"))
    {
        var provider = ground.GetProperty("provider");
        var order = provider.TryGetProperty("order", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Array
            ? o.EnumerateArray().Select(x => x.GetString()).ToArray()
            : Array.Empty<string?>();
        Check(order.Length == 2 && order[0] == "cerebras" && order[1] == "groq",
            $"provider.order is [cerebras, groq] in that order (got [{string.Join(", ", order)}])");
        Check(provider.TryGetProperty("allow_fallbacks", out var af)
            && af.ValueKind == System.Text.Json.JsonValueKind.True,
            "allow_fallbacks defaults to true when it is not configured");
    }
    Check(Str(ground, "reasoning_effort") == "medium", "grounding STREAMS and still carries reasoning_effort");
    Check(ground.TryGetProperty("stream", out var streamFlag)
        && streamFlag.ValueKind == System.Text.Json.JsonValueKind.True,
        "the handler ADDED provider without disturbing what SK wrote (\"stream\": true survives)");

    var compose = await SendAsync(routed, LlmCallSite.Compose, streaming: false);
    Check(Has(compose, "provider"), "compose carries provider too — routing is process-wide");
    Check(!Has(compose, "reasoning_effort"),
        "LLM_REASONING_EFFORT_COMPOSE=off suppresses the global default at that ONE site");

    // --- "cerebras or nothing", which is what the demo ships ---
    //
    // The mechanism's DEFAULT is allow_fallbacks:true, but the demo turns it off, and the
    // measurement is why: Cerebras plans a goal in 8-10s and the next-best provider takes
    // 203-234s — slower than sending no preference at all. Falling back is not graceful
    // degradation here, so a run that cannot have Cerebras should fail visibly instead.
    var strictEnv = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["OPENROUTER_PROVIDER_ORDER"] = "cerebras",
        ["OPENROUTER_PROVIDER_ALLOW_FALLBACKS"] = "false",
    };
    var strict = LlmRouting.FromEnvironment(k => strictEnv.TryGetValue(k, out var v) ? v : null);
    var strictBody = await SendAsync(strict, LlmCallSite.Compose, streaming: false);
    if (Has(strictBody, "provider"))
    {
        var sp = strictBody.GetProperty("provider");
        var order = sp.TryGetProperty("order", out var so) && so.ValueKind == System.Text.Json.JsonValueKind.Array
            ? so.EnumerateArray().Select(x => x.GetString()).ToArray()
            : Array.Empty<string?>();
        Check(order.Length == 1 && order[0] == "cerebras", "strict: exactly one provider is named");
        Check(sp.TryGetProperty("allow_fallbacks", out var saf)
            && saf.ValueKind == System.Text.Json.JsonValueKind.False,
            "strict: ALLOW_FALLBACKS=false reaches the wire as false, so a busy Cerebras fails loudly");
    }
    else
    {
        Check(false, "strict: provider block reached the wire");
    }

    // --- fail-soft: a bad value degrades to unset, it never throws ---
    var garbage = LlmRouting.FromEnvironment(k => k == "OPENROUTER_PROVIDER_JSON" ? "{not json" : null);
    Check(garbage.IsNoOp, "malformed OPENROUTER_PROVIDER_JSON is ignored, not thrown");
    var badEffort = LlmRouting.FromEnvironment(k => k == "LLM_REASONING_EFFORT" ? "lo" : null);
    Check(badEffort.IsNoOp, "an unknown reasoning_effort value is ignored, not thrown");

    stop.Cancel();
    listener.Stop();
    try { await serve.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }

    Console.WriteLine(failures == 0 ? "gate 29 (request shape): PASS" : $"gate 29 (request shape): FAIL: {failures}");
    return failures == 0 ? 0 : 1;
}

}
