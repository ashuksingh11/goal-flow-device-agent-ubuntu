using GoalFlow.Device.Agent;
using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Capabilities;
using GoalFlow.Device.Modules.Steering;
using GoalFlow.Device.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

// Mock world + capability plugins (meal domain + shared).
services.AddSingleton(sp => new MockWorldStore(options.DataDir, sp.GetRequiredService<IClock>()));
services.AddSingleton<InventoryPlugin>();
services.AddSingleton<CalendarPlugin>();
services.AddSingleton<RecipePlugin>();
services.AddSingleton<ShoppingListPlugin>();
services.AddSingleton<ReminderPlugin>();
services.AddSingleton<GuestsPlugin>();
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
    provider.GetRequiredService<IClock>(),
    loggerFactory.CreateLogger<GoalAgent>());

if (options.ConnectUrl is { } url)
{
    var deviceId = ProgramHelpers.ResolveDeviceId(options.DeviceId, options.DataDir);
    var deviceName = ProgramHelpers.ResolveDeviceName(options.DeviceName, deviceId);
    loggerFactory.CreateLogger("Connect").LogInformation("device_id={DeviceId} device_name={DeviceName}", deviceId, deviceName);
    await using var ws = new WsClient(new Uri(url), loggerFactory.CreateLogger<WsClient>(), deviceId, deviceName);
    liveWs = ws;
    var capabilities = provider.GetRequiredService<CapabilityRegistry>().BuildCapabilitiesMessage(kernel);
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
        var generated = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(path, generated);
        return generated;
    }
    catch
    {
        // Non-persistent fallback (e.g. read-only data dir): still unique per run.
        return Guid.NewGuid().ToString("N");
    }
}

/// <summary>A human label for the device picker: <c>--device-name</c> / <c>$DEVICE_NAME</c>,
/// else a short friendly default derived from the id.</summary>
public static string ResolveDeviceName(string? cliValue, string deviceId)
{
    var configured = cliValue ?? Environment.GetEnvironmentVariable("DEVICE_NAME");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured.Trim();
    }
    var suffix = deviceId.Length <= 6 ? deviceId : deviceId[..6];
    return $"GoalFlow Hub {suffix}";
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
