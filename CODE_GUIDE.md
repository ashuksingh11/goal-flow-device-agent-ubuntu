# Code Guide — goal-flow-device-agent-ubuntu

The **on-device agent** is the core IP of GoalFlow: a .NET 8 + Semantic Kernel process that receives
a Task Contract, runs a **harness pipeline** (sense → decide → gate → act → sustain) to produce and
execute a plan, and is the *sole authority on local state*. It's built Linux-first for fast dev; the
Tizen port swaps only the adapter implementations. See
`../goal-flow-agents/docs/SYSTEM_OVERVIEW.md` for the whole system.

> **Note:** `README.md` describes M1 scope; this guide reflects the finished M1–M4 build.

## File map

```
GoalFlow.Device.sln
data/                              # mock world (seed JSON) + fixtures
  inventory.json calendar.json recipes.json shopping_list.json reminders.json
  sample-contract.json golden-plan_ready.json sample-approval.json
src/GoalFlow.Device/
  Program.cs                       # CLI entry + DI wiring (BuildPipeline)         ← start here
  Pipeline.cs                      # orchestrator: RunAsync / OnApprovalAsync / AdvanceDayAsync / OnControlAsync
  WorldState.cs                    # the normalized state Grounding produces
  Contracts/                       # C# mirror of CONTRACT.md (Dispatch, PlanReady, Proposal, Approval, Status, Control, …)
  Harnesses/
    IClock.cs / VirtualClock       # virtual clock (NEVER wall-clock)
    Grounding.cs                   # assembles WorldState from adapters
    IPlanner.cs                    # + RulesPlanner / LlmPlanner / ScriptedPlanner
    SafetyGate.cs                  # deterministic hard-constraint check (separate from planner)
    ApprovalBroker.cs              # proposal lifecycle: pending → approved → executed
    EffectExecutor.cs              # performs approved effects idempotently
    Scheduler.cs / ChangeWatcher.cs# sustain loop + materiality policy
    Trace.cs                       # structured log (stderr; feeds presenter feed)
    Adapters/                      # Mock{Inventory,Calendar,Recipe,ShoppingList,Reminder}Api
  Transport/WsClient.cs            # outbound WebSocket client (thin transport shell)
docs/ARCHITECTURE.md, docs/HARNESSES.md, docs/diagrams.md
```

## Entry point & modes (`Program.cs`)

`Program.cs` parses CLI options, loads `.env`, builds a `VirtualClock` (anchor `2026-07-12`), wires
the pipeline via `BuildPipeline(...)`, and runs one of these modes:

| Command | What it does |
|---|---|
| `dotnet run -- --contract data/sample-contract.json` | Read a Task Contract, run the pipeline, print `plan_ready` JSON to **stdout** (logs to stderr). |
| `… --planner rules\|llm\|scripted` | Choose the planner (default `rules`). |
| `… --simulate-approval data/sample-approval.json [--replay-simulated-approval]` | Feed an approval after planning (the `--replay` flag proves idempotency: second approval adds nothing). |
| `dotnet run -- --connect` | Run the outbound **WebSocket** loop (`WS_URL`, default `ws://localhost:8000/ws`): handle `dispatch`/`approval`/`control`, emit `plan_ready`/`status`/`proposal`. This is the demo mode. |
| `dotnet run -- --simulate-week` | Headless M4: advance the virtual clock Mon→Fri, print each sustain `status` and Wednesday's adaptation `proposal`, then reset. Uses a temp data copy (never dirties `data/`). |

## The pipeline (`Pipeline.cs`)

`BuildPipeline` (in `Program.cs`) injects every harness into the `Pipeline`. The main methods:

- **`RunAsync(dispatch)`** — the plan path:
  `Grounding` builds a `WorldState` → the selected `IPlanner` produces a candidate plan →
  **`SafetyGate.Check`** validates it against `constraints.hard` (deterministic code) →
  `ApprovalBroker` freezes side-effects as pending proposals → returns `plan_ready` (with `impact`).
- **`OnApprovalAsync(approval)`** — on approved proposals, `EffectExecutor` performs the side-effect
  idempotently (dedupe on `correlation_id:proposal_id`) against `shopping_list.json`/`reminders.json`,
  and emits `status` (`executing` → `done`) with the executed results.
- **`AdvanceDayAsync(goalId)`** — one sustain tick: `Scheduler` advances the clock a day, reconciles
  inventory, checks that day's calendar, and asks `ChangeWatcher` whether anything is **material**.
  Returns a `status` (monitoring) and, if material, an adaptation `proposal`.
- **`OnControlAsync(control)`** — handles `advance_day` (→ `AdvanceDayAsync`) and `reset` (restore
  seed data + clock).

## The two-gate rule (why the code is shaped this way)

`SafetyGate` and the planners are **separate classes**. The planner (rules or LLM) is fallible; the
safety gate is deterministic code that reads *only* `constraints.hard` and the recipe facts in
`recipes.json` (e.g. `contains_pork`, allergen flags), and **blocks** on violation
(`gate: "blocked"`, with `hard_violations`). The LLM never runs the safety check. This is the
"LLM plans, code checks" guarantee — verify it: set a hard constraint that a seeded recipe violates
and the gate blocks the plan.

## The adaptation beat (`ChangeWatcher.cs`)

Materiality policy: *a calendar event overlapping a planned dish's prep window makes that day
material.* On the demo week only **Wednesday** (Aarav's football 18:00) is material → the watcher
emits an adaptation proposal ("prep Wed's dish before the evening crunch"). Quiet days return
`material: false`. This is the "4 quiet days, 1 smart Wednesday" judgment moment.

## Portability (toward Tizen)

Everything platform-specific sits behind interfaces (`IClock`, the adapter interfaces, storage). The
Tizen port replaces the `Mock*Api` adapters and the clock/storage implementations with real Tizen
device APIs — the harness logic in `Pipeline.cs` and the gates stay unchanged. Never call
`DateTime.Now`; always read `IClock`.

## Run & verify

```bash
dotnet build
dotnet run -- --contract data/sample-contract.json --planner rules   # plan_ready with safety passed + impact
dotnet run -- --simulate-week                                          # the whole adaptation week, headless
WS_URL=ws://localhost:8000/ws dotnet run --no-build -- --connect       # attach to a running cloud
```

## Extending it

- **Real actuators:** implement `EffectExecutor` / the adapters against real device APIs.
- **New harness:** add an interface + implementation under `Harnesses/`, inject it in
  `BuildPipeline`, and call it from the relevant `Pipeline` method.
- **Smarter planning:** improve `RulesPlanner`, or tune the `LlmPlanner` prompt — the `SafetyGate`
  guarantee holds regardless of which planner runs.
