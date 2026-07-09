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
var clock = new VirtualClock(DateTimeOffset.Parse("2026-07-12T09:00:00+00:00"));
var trace = new InMemoryTrace();
var pipeline = BuildPipeline(options, clock, trace);

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
Console.WriteLine(JsonSerializer.Serialize(planReady, ContractJson.Options));

static Pipeline BuildPipeline(CliOptions options, IClock clock, ITrace trace)
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

    var grounding = new Grounding(
        new MockInventoryApi(Path.Combine(options.DataDir, "inventory.json")),
        new MockCalendarApi(Path.Combine(options.DataDir, "calendar.json")),
        new MockRecipeApi(Path.Combine(options.DataDir, "recipes.json")),
        new MockShoppingListApi(Path.Combine(options.DataDir, "shopping_list.json")),
        new MockReminderApi(Path.Combine(options.DataDir, "reminders.json")),
        clock,
        trace);

    return new Pipeline(planner, grounding, new SafetyGate(trace, clock), new ApprovalBroker(clock, trace), clock, trace);
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

    public static CliOptions Parse(string[] args)
    {
        // Minimal deliberate parser — no external dependency.
        string? contract = null;
        var connect = false;
        var planner = "rules";
        var dataDir = "data";
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
            }
        }

        if (!connect && contract is null)
        {
            Console.Error.WriteLine("usage: dotnet run -- --contract <file.json> [--planner rules|llm|scripted] [--data <dir>]");
            Console.Error.WriteLine("   or: dotnet run -- --connect [--planner rules|llm|scripted] [--data <dir>]  (WS_URL defaults to ws://localhost:8000/ws)");
            Environment.Exit(2);
        }

        return new CliOptions { ContractPath = contract, Connect = connect, Planner = planner, DataDir = dataDir };
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
