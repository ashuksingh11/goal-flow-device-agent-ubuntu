using GoalFlow.Device;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harnesses;
using GoalFlow.Device.Transport;
using System.Text.Json;

// GoalFlow device agent — Milestone 1 command-line entry (DESIGN STUB).
//
// Usage:
//   dotnet run -- --contract path/to/contract.json [--planner rules|llm|scripted] [--data ./data]
//
// M1 behavior: read a Task Contract JSON (a `dispatch` message), run the
// harness pipeline, print the resulting `plan_ready` JSON to stdout.
// A canned plan (ScriptedPlanner) is acceptable for M1.
// The WebSocket shell (Transport/WsClient.cs) is snapped on in a later
// milestone and drives this exact same Pipeline.

var options = CliOptions.Parse(args);
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
    IPlanner planner = options.Planner switch
    {
        "scripted" or "rules" => new ScriptedPlanner(fixturePath),
        "llm" => throw new NotImplementedException("LlmPlanner stays unimplemented until a later milestone."),
        _ => throw new ArgumentException($"Unknown planner '{options.Planner}'. Use scripted for M1."),
    };

    return new Pipeline(planner, new SafetyGate(trace, clock), clock, trace);
}

/// <summary>Parsed command-line options for the M1 entry point.</summary>
internal sealed record CliOptions
{
    /// <summary>--contract: path to a dispatch/Task Contract JSON.</summary>
    public string? ContractPath { get; init; }

    /// <summary>--connect: run the outbound WebSocket transport loop.</summary>
    public bool Connect { get; init; }

    /// <summary>--planner (optional): scripted for M1. Default: scripted.</summary>
    public string Planner { get; init; } = "scripted";

    /// <summary>--data (optional): mock-world directory. Default: ./data.</summary>
    public string DataDir { get; init; } = "data";

    public static CliOptions Parse(string[] args)
    {
        // Minimal deliberate parser — no external dependency.
        string? contract = null;
        var connect = false;
        var planner = "scripted";
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
            Console.Error.WriteLine("usage: dotnet run -- --contract <file.json> [--planner scripted] [--data <dir>]");
            Console.Error.WriteLine("   or: dotnet run -- --connect [--data <dir>]  (WS_URL defaults to ws://localhost:8000/ws)");
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
