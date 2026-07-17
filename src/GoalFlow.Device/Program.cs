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
services.AddSingleton<SafetyFilter>();
services.AddSingleton<ApprovalCoordinator>();
services.AddSingleton<Grounding>();
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
    provider.GetRequiredService<ApprovalCoordinator>(),
    provider.GetRequiredService<MonitorAdapt>(),
    provider.GetRequiredService<CapabilityManager>(),
    tasks,
    provider.GetRequiredService<PrecheckEngine>(),
    provider.GetRequiredService<IClock>(),
    loggerFactory.CreateLogger<GoalAgent>());

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
                        var (status, proposal) = await agent.HandleControlAsync(ContractJson.Deserialize<Control>(raw));
                        await ws.SendAsync(status);
                        if (proposal is not null) await ws.SendAsync(proposal);
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

    /// <summary>--verify-task-lifecycle — assert the task DAG, legal moves and derived progress (M2 gate).</summary>
    public bool VerifyTaskLifecycle { get; init; }

    /// <summary>--verify-prechecks — assert the runtime gates pass, block and defer correctly (M3 gate).</summary>
    public bool VerifyPrechecks { get; init; }

    /// <summary>--verify-trace-isolation — assert concurrent goals don't collide on goal_id/seq (M5 gate).</summary>
    public bool VerifyTraceIsolation { get; init; }

    /// <summary>--verify-deadline — assert a stalled provider stream aborts rather than wedging a goal (M6 gate).</summary>
    public bool VerifyDeadline { get; init; }

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
                "--verify-task-lifecycle" => options with { VerifyTaskLifecycle = true },
                "--verify-prechecks" => options with { VerifyPrechecks = true },
                "--verify-trace-isolation" => options with { VerifyTraceIsolation = true },
                "--verify-deadline" => options with { VerifyDeadline = true },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'.")
            };
        }

        return options;
    }
}

/// <summary>Minimal KEY=VALUE .env loader (BCL only; missing file is fine).</summary>
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
        if (Directory.Exists(dataDir) && Directory.EnumerateFiles(dataDir, "*.json").Any())
        {
            return; // already has a world
        }
        if (!Directory.Exists(seed))
        {
            return; // nothing to seed from — let the store fail loudly
        }
        Directory.CreateDirectory(dataDir);
        foreach (var file in Directory.EnumerateFiles(seed, "*.json"))
        {
            File.Copy(file, Path.Combine(dataDir, Path.GetFileName(file)), overwrite: false);
        }
        Console.Error.WriteLine($"seeded mock world: {dataDir} <- {seed}");
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

public static Dispatch BuildLocalDispatch(string goal, string domain, IClock clock)
{
    var start = clock.Today.AddDays(1).ToString("yyyy-MM-dd");
    var end = clock.Today.AddDays(5).ToString("yyyy-MM-dd");
    return new Dispatch
    {
        GoalId = $"local-{Guid.NewGuid():N}",
        CorrelationId = $"local-{Guid.NewGuid():N}",
        Domain = domain,
        Objective = goal,
        SuccessCriteria = ["weekday dinners planned", "expiring inventory used", "shopping proposals tiered"],
        Constraints = new TaskConstraints
        {
            Hard = new JsonObject
            {
                ["allergens"] = new JsonArray(),
                ["dietary"] = new JsonArray(),
                ["medical"] = new JsonArray(),
                ["budget_cap"] = 60.0
            },
            Soft = new JsonObject { ["prefer"] = new JsonArray("more_vegetables", "more_protein") }
        },
        Scope = new JsonObject { ["meal"] = "dinner", ["days"] = new JsonArray("Mon", "Tue", "Wed", "Thu", "Fri") },
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

}
