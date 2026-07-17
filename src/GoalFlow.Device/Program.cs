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
services.AddSingleton<MaterialityPolicy>();
services.AddSingleton<MonitorAdapt>();

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
var agent = new GoalAgent(
    kernel,
    trace,
    provider.GetRequiredService<Grounding>(),
    provider.GetRequiredService<SafetyFilter>(),
    provider.GetRequiredService<ApprovalCoordinator>(),
    provider.GetRequiredService<MonitorAdapt>(),
    provider.GetRequiredService<CapabilityManager>(),
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

}
