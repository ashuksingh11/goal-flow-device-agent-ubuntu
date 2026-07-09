# GoalFlow Device Agent (Ubuntu / .NET 8)

The **on-device agent** for the GoalFlow POC — a two-tier goal-based agent
orchestration demo (Samsung Tizen Family Hub). The cloud agent owns the
conversation and memory and decomposes a user goal into a **Task Contract**;
this device agent is the **sole authority on local state** and the **only
component that touches actuators**. It runs a harness pipeline
(sense → decide → gate → act → sustain) over a mocked local world.

Ethos: *"fake the world; make the mechanism real."*

Key invariants:

- The UI and the device **never** talk directly — everything routes through
  the cloud. The cloud is the WebSocket hub; the device opens **one** outbound
  WebSocket to it (`System.Net.WebSockets.ClientWebSocket`, works on both
  Linux dev boxes and Tizen).
- **Two distinct gates, two classes.** The **safety gate** is deterministic
  code that blocks on hard-constraint violations. The **approval gate** is the
  user, reached via the cloud. The planner never runs the safety check —
  *"LLM plans, code checks."*
- Side-effects leave the device as **proposals**, never actions. Nothing
  executes until an approval comes back.
- Anything Tizen-specific (device APIs, local storage, wall-clock) sits behind
  **injectable interfaces** so the Tizen port is an adapter swap. The device
  never reads the wall-clock directly — always the virtual `IClock`.
- The planner is **swappable** (`IPlanner`): `RulesPlanner` (default,
  deterministic, demo-safe) or `LlmPlanner` (Semantic Kernel + OpenRouter),
  with a `ScriptedPlanner` canned fallback.

## Milestones

**M1 (current target) — command line, no transport.** The device agent is
developed and tested via the CLI, fully decoupled from transport:

```bash
dotnet run --project src/GoalFlow.Device -- --contract data/sample-contract.json
```

reads a Task Contract JSON (a `dispatch` message) and prints a `plan_ready`
JSON to stdout. A canned plan is fine for M1. Compare against
`data/golden-plan_ready.json`.

**Later — WebSocket shell.** `Transport/WsClient.cs` is a thin wrapper snapped
on top: it deserializes incoming `dispatch` frames → calls the same pipeline →
serializes `plan_ready` back. The pipeline itself never changes.

**M4 — sustain.** `Scheduler` (virtual-clock timers) and `ChangeWatcher`
(materiality-filtered world-change re-planning) come alive.

## Build & run

Requires the .NET 8 SDK.

```bash
dotnet build GoalFlow.Device.sln
dotnet run --project src/GoalFlow.Device -- --contract data/sample-contract.json
```

## Environment

Only needed when the `LlmPlanner` is selected (the default `RulesPlanner`
needs no network or keys). Copy `.env.example` and fill in:

| Variable              | Meaning                                   | Default                        |
| --------------------- | ----------------------------------------- | ------------------------------ |
| `OPENROUTER_API_KEY`  | OpenRouter API key                        | — (required for LlmPlanner)    |
| `OPENROUTER_BASE_URL` | OpenAI-compatible base URL                | `https://openrouter.ai/api/v1` |
| `OPENROUTER_MODEL`    | Model id                                  | `anthropic/claude-sonnet-5`    |

## Layout

```
src/GoalFlow.Device/
  Contracts/    C# mirror of CONTRACT v0 (canonical CONTRACT.md lives in the cloud repo)
  Harnesses/    one file per harness: interface + skeleton class (incl. IClock, IPlanner)
    Adapters/   product API adapters (inventory, calendar, recipes, shopping list, reminders)
  Transport/    WsClient — thin WS shell (later milestone)
  Pipeline.cs   orchestrator: sense → decide → gate → act
  WorldState.cs the normalized world model produced by Grounding
data/           seed mock world + sample contract + golden plan_ready fixture
docs/           ARCHITECTURE.md, HARNESSES.md, diagrams.md
```

Status: **design skeleton** — interfaces are fully specified; class bodies are
`NotImplementedException` stubs. Implementation happens in a later phase.
