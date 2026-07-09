using GoalFlow.Device;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harnesses;
using GoalFlow.Device.Harnesses.Adapters;
using GoalFlow.Device.Transport;
using System.Text.Json;

// GoalFlow device agent — Milestone 1 command-line entry (DESIGN STUB).
//
// Usage:
//   dotnet run -- --contract path/to/contract.json [--planner rules|llm|scripted] [--data ./data]
//
// Contract mode prints only the resulting `plan_ready` JSON to stdout.
// Trace/log output goes to stderr so harnesses can script against stdout.

var options = CliOptions.Parse(args);
LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
var clockAnchor = DateTimeOffset.Parse("2026-07-12T09:00:00+00:00");
var clock = new VirtualClock(clockAnchor);
var trace = new InMemoryTrace();
var runDataDir = options.DataDir;
string? tempDataDir = null;
if (options.SimulateWeek)
{
    tempDataDir = Path.Combine(Path.GetTempPath(), $"goalflow-m4-{Guid.NewGuid():N}");
    CopyDirectory(options.DataDir, tempDataDir);
    runDataDir = tempDataDir;
}

var seedStore = new DataSeedStore(runDataDir);
var pipeline = BuildPipeline(options with { DataDir = runDataDir }, clock, trace, clockAnchor, seedStore.Restore);

if (options.SimulateWeek)
{
    try
    {
        var simulateContractJson = await File.ReadAllTextAsync(options.ContractPath ?? Path.Combine(runDataDir, "sample-contract.json"));
        var simulateDispatch = JsonSerializer.Deserialize<Dispatch>(simulateContractJson, ContractJson.Options)
            ?? throw new InvalidOperationException("Unable to deserialize simulate-week contract.");
        await pipeline.RunAsync(simulateDispatch);
        for (var i = 0; i < 5; i++)
        {
            var result = await pipeline.AdvanceDayAsync(simulateDispatch.GoalId);
            Console.WriteLine(JsonSerializer.Serialize(result.Status, ContractJson.Options));
            if (result.Proposal is not null)
            {
                Console.WriteLine(JsonSerializer.Serialize(result.Proposal, ContractJson.Options));
            }
        }

        var reset = new Control { GoalId = simulateDispatch.GoalId, Command = ControlCommands.Reset };
        foreach (var message in await pipeline.OnControlAsync(reset))
        {
            Console.WriteLine(JsonSerializer.Serialize(message, ContractJson.Options));
        }
    }
    finally
    {
        if (tempDataDir is not null && Directory.Exists(tempDataDir))
        {
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    return;
}

if (options.Connect)
{
    var wsUrl = Environment.GetEnvironmentVariable("WS_URL") ?? "ws://localhost:8000/ws";
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cts.Cancel();
    };

    await using var client = new WsClient(new Uri(wsUrl), pipeline, trace);
    await client.RunAsync(cts.Token);
    return;
}

var contractJson = await File.ReadAllTextAsync(options.ContractPath!);
var dispatch = JsonSerializer.Deserialize<Dispatch>(contractJson, ContractJson.Options)
    ?? throw new InvalidOperationException($"Unable to deserialize dispatch contract '{options.ContractPath}'.");

var planReady = await pipeline.RunAsync(dispatch);
if (options.SimulateApprovalPath is not null)
{
    var approvalJson = await File.ReadAllTextAsync(options.SimulateApprovalPath);
    var approval = JsonSerializer.Deserialize<Approval>(approvalJson, ContractJson.Options)
        ?? throw new InvalidOperationException($"Unable to deserialize approval '{options.SimulateApprovalPath}'.");
    foreach (var status in await pipeline.OnApprovalAsync(approval))
    {
        Console.WriteLine(JsonSerializer.Serialize(status, ContractJson.Options));
    }

    if (options.ReplaySimulatedApproval)
    {
        foreach (var status in await pipeline.OnApprovalAsync(approval))
        {
            Console.WriteLine(JsonSerializer.Serialize(status, ContractJson.Options));
        }
    }

    return;
}

Console.WriteLine(JsonSerializer.Serialize(planReady, ContractJson.Options));

static Pipeline BuildPipeline(CliOptions options, IClock clock, ITrace trace, DateTimeOffset clockAnchor, Action? resetData)
{
    var fixturePath = Path.Combine(options.DataDir, "golden-plan_ready.json");
    var rulesPlanner = new RulesPlanner(trace, clock);
    var scriptedPlanner = new ScriptedPlanner(fixturePath);
    IPlanner planner = options.Planner switch
    {
        "rules" => rulesPlanner,
        "scripted" => scriptedPlanner,
        "llm" => new LlmPlanner(
            new LlmPlannerOptions
            {
                ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
                BaseUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ?? "https://openrouter.ai/api/v1",
                Model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "anthropic/claude-sonnet-5",
            },
            rulesPlanner,
            trace,
            clock),
        _ => throw new ArgumentException($"Unknown planner '{options.Planner}'. Use rules, llm, or scripted."),
    };

    var shoppingList = new MockShoppingListApi(Path.Combine(options.DataDir, "shopping_list.json"));
    var reminders = new MockReminderApi(Path.Combine(options.DataDir, "reminders.json"));
    var grounding = new Grounding(
        new MockInventoryApi(Path.Combine(options.DataDir, "inventory.json")),
        new MockCalendarApi(Path.Combine(options.DataDir, "calendar.json")),
        new MockRecipeApi(Path.Combine(options.DataDir, "recipes.json")),
        shoppingList,
        reminders,
        clock,
        trace);

    return new Pipeline(
        planner,
        grounding,
        new SafetyGate(trace, clock),
        new ApprovalBroker(clock, trace),
        new EffectExecutor(shoppingList, reminders, clock, trace),
        new Scheduler(clock, trace),
        new ChangeWatcher(clock, trace),
        clock,
        trace,
        clockAnchor,
        resetData);
}

static void CopyDirectory(string sourceDir, string targetDir)
{
    Directory.CreateDirectory(targetDir);
    foreach (var file in Directory.EnumerateFiles(sourceDir))
    {
        File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
    }
}

static void LoadDotEnv(string path)
{
    if (!File.Exists(path))
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(path))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var equals = line.IndexOf('=');
        if (equals <= 0)
        {
            continue;
        }

        var key = line[..equals].Trim();
        var value = line[(equals + 1)..].Trim().Trim('"');
        if (Environment.GetEnvironmentVariable(key) is null)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

internal sealed class DataSeedStore
{
    private readonly string _dataDir;
    private readonly Dictionary<string, byte[]> _seed = new(StringComparer.Ordinal);

    public DataSeedStore(string dataDir)
    {
        _dataDir = dataDir;
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.json"))
        {
            _seed[Path.GetFileName(file)] = File.ReadAllBytes(file);
        }
    }

    public void Restore()
    {
        Directory.CreateDirectory(_dataDir);
        foreach (var file in Directory.EnumerateFiles(_dataDir, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (!_seed.ContainsKey(name))
            {
                File.Delete(file);
            }
        }

        foreach (var (name, bytes) in _seed)
        {
            File.WriteAllBytes(Path.Combine(_dataDir, name), bytes);
        }
    }
}

/// <summary>Parsed command-line options for the M1 entry point.</summary>
internal sealed record CliOptions
{
    /// <summary>--contract: path to a dispatch/Task Contract JSON.</summary>
    public string? ContractPath { get; init; }

    /// <summary>--connect: run the outbound WebSocket transport loop.</summary>
    public bool Connect { get; init; }

    /// <summary>--planner (optional): rules, llm, or scripted. Default: rules.</summary>
    public string Planner { get; init; } = "rules";

    /// <summary>--data (optional): mock-world directory. Default: ./data.</summary>
    public string DataDir { get; init; } = "data";

    /// <summary>--simulate-approval: path to approval JSON to feed after planning.</summary>
    public string? SimulateApprovalPath { get; init; }

    /// <summary>--replay-simulated-approval: feed the same approval twice for idempotency verification.</summary>
    public bool ReplaySimulatedApproval { get; init; }

    /// <summary>--simulate-week: headless M4 virtual-clock sustain demo.</summary>
    public bool SimulateWeek { get; init; }

    public static CliOptions Parse(string[] args)
    {
        // Minimal deliberate parser — no external dependency.
        string? contract = null;
        var connect = false;
        var planner = "rules";
        var dataDir = "data";
        string? simulateApproval = null;
        var replaySimulatedApproval = false;
        var simulateWeek = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--contract":
                    contract = RequireValue(args, ref i, "--contract");
                    break;
                case "--connect":
                    connect = true;
                    break;
                case "--planner":
                    planner = RequireValue(args, ref i, "--planner");
                    break;
                case "--data":
                    dataDir = RequireValue(args, ref i, "--data");
                    break;
                case "--simulate-approval":
                    simulateApproval = RequireValue(args, ref i, "--simulate-approval");
                    break;
                case "--replay-simulated-approval":
                    replaySimulatedApproval = true;
                    break;
                case "--simulate-week":
                    simulateWeek = true;
                    break;
            }
        }

        if (!connect && !simulateWeek && contract is null)
        {
            Console.Error.WriteLine("usage: dotnet run -- --contract <file.json> [--planner rules|llm|scripted] [--data <dir>] [--simulate-approval <approval.json>]");
            Console.Error.WriteLine("   or: dotnet run -- --connect [--planner rules|llm|scripted] [--data <dir>]  (WS_URL defaults to ws://localhost:8000/ws)");
            Console.Error.WriteLine("   or: dotnet run -- --simulate-week [--data <dir>]");
            Environment.Exit(2);
        }

        return new CliOptions
        {
            ContractPath = contract,
            Connect = connect,
            Planner = planner,
            DataDir = dataDir,
            SimulateApprovalPath = simulateApproval,
            ReplaySimulatedApproval = replaySimulatedApproval,
            SimulateWeek = simulateWeek,
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }
}
