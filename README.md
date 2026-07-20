# GoalFlow Device Agent (Ubuntu / .NET 8) — v3

The **on-device agent** for GoalFlow — the **executor tier of a general
goal agent** for the Samsung Family Hub. The cloud agent owns the conversation
and memory and decomposes a fuzzy user goal into a generic **Task Contract**
(the `dispatch` frame); this device agent is the sole authority on local state
and the only component that touches actuators.

Ethos: *"fake the world; make the mechanism real."* The world is mock JSON;
the agent mechanics — SK auto function-calling, a deterministic safety filter,
tiered approvals, a sustain loop — are real.

**Core idea: the device IS a Semantic Kernel agent.** There is no
hand-rolled pipeline and no rules/scripted planner. Device capabilities are SK
**plugins** whose methods are `[KernelFunction]`s the LLM *calls* via **auto
function-calling** (`FunctionChoiceBehavior.Auto`); safety is a deterministic
SK **`IFunctionInvocationFilter`** that vets every pending call against the
contract's `constraints.hard` before the plugin method runs.

Key invariants:

- **LLM-only planning.** Every plan comes from one SK agent run against
  OpenRouter. No `RulesPlanner`, no `ScriptedPlanner`, no fallbacks — the
  harness modules *steer and guard* the LLM instead of replacing it.
- **Two gates, two mechanisms.** The **safety gate** is the `SafetyFilter`
  (deterministic code in the kernel's invocation pipeline — *"LLM plans, code
  checks"*). The **approval gate** is the user: side-effecting calls are
  frozen into **tiered proposals** (`auto` / `light` / `firm`) and nothing
  irreversible executes before an `approval` frame comes back.
- **General agent, not a meal app.** Six domains ship — `meal_plan`,
  `guest_dinner`, `vacation_prep`, `birthday_party`, `grocery_cost` and
  `energy_saving` — running through the *same* kernel host, harness components,
  and protocol. Domains differ only in which capability plugins the planner
  leans on and which `IDomainObserver` watches them; adding a domain =
  registering an observer (that registration IS the domain the cloud routes on).
- **Generic clock.** Nothing hardcodes a date. `IClock` is the real system
  clock by default, or a `SimulatedClock` driven by `--date` / `control`
  frames (`set_date`, `advance_day`). Mock data stores day *offsets* resolved
  against the clock at read time, so the seed world is always "this week".
- **Tizen-lean.** The only NuGet dependencies are `Microsoft.SemanticKernel`
  and `Microsoft.Extensions.Logging`/`.DependencyInjection`; everything else
  is BCL (`System.Text.Json`, `System.Net.WebSockets`). The process must port
  to Tizen.NET as-is.
- **The UI and the device never talk directly** — the cloud is the WebSocket
  hub; the device opens one outbound `ClientWebSocket` and streams
  `agent_event` frames (phase / thinking / tool_call / tool_result /
  plan_progress) so the UI can watch it think.
- **Multi-session.** The cloud hub now serves many device agents and many UIs
  at once, paired by `device_id` (a "home" = 1 device + N UIs). This agent
  identifies itself in `hello` with `device_id` (`--device-id` / `$DEVICE_ID`,
  else a persistent self-generated UUID in `<data>/device_id`) and
  `device_name` (`--device-name` / `$DEVICE_NAME`, else `user@machine (<short-id>)` — shown
  in the UI's device picker when more than one agent is online).
- **Event-driven meal demo.** `plan_ready.payload.demo_events` (from
  `data/daily_events.json`) advertises a small catalog of presenter-fired
  events as UI chips (restock, a spoiled ingredient, a calendar clash, a
  guest, an unavailable appliance, a "lighter dinner" request). Firing one
  sends `control: trigger_event { event_id }`; `GoalAgent.HandleControlCoreAsync`
  looks up the event, checks materiality, and — if material and not already
  applied — runs one **scoped** LLM re-plan against just that event's
  `context` + `steer` (the clock does not move), returning an adaptation
  `proposal` (a minimal `PlanPatch`) the same way the sustain loop does.

## Build & run

Requires the .NET 8 SDK and OpenRouter credentials (planning is LLM-only, so
`OPENROUTER_API_KEY` is always required — see Environment below).

**Always pass `--project GoalFlow.Device.csproj`** — a bare `dotnet run --no-build`
from the repo root can execute a stale binary (the root csproj and the sln use
different output paths). For the **full three-service demo** (cloud + device + UI),
follow `goal-flow-agents/docs/FINAL_DEMO.md` — the single source of truth for run
commands; the commands below are for driving the device on its own.

```bash
dotnet build GoalFlow.Device.csproj

# One-shot plan from a natural-language goal (local dispatch is synthesized):
dotnet run --project GoalFlow.Device.csproj -- --goal "help us eat healthier this week" [--domain meal_plan]

# One-shot plan from a Task Contract file (plan_ready JSON on stdout):
dotnet run --project GoalFlow.Device.csproj -- --contract data/sample-contract.json
dotnet run --project GoalFlow.Device.csproj -- --contract data/sample-contract-guest.json

# ... optionally apply (and replay) an approval afterwards:
dotnet run --project GoalFlow.Device.csproj -- --contract data/sample-contract.json --approval data/sample-approval.json

# Live session: dial a running cloud hub. Bare --connect defaults to
# ws://localhost:8000/ws; pass a URL (or set $WS_URL) for a remote cloud:
dotnet run --project GoalFlow.Device.csproj -- --connect
dotnet run --project GoalFlow.Device.csproj -- --connect ws://<cloud-ip>:8000/ws

# Headless sustain demos (plan, then advance days; adaptation on the material day).
# Both run on a temp copy of data/ so the seed world is never dirtied:
dotnet run --project GoalFlow.Device.csproj -- --simulate-week    # meal_plan: 5 weekday ticks
dotnet run --project GoalFlow.Device.csproj -- --simulate-guest   # guest_dinner: 2 ticks to the RSVP/late-arrival trigger

# Extras: [--date 2026-07-14] start a SimulatedClock there; [--data ./data]; [--verbose]
# [--device-id <id>] / [--device-name <name>] override the auto-resolved pairing identity.

# Two agents side by side (multi-session test): each needs its own --data dir so
# their mock worlds don't clobber each other — a fresh dir auto-seeds from ./data.
dotnet run --project GoalFlow.Device.csproj -- --connect ws://localhost:8000/ws --data ./data-a
dotnet run --project GoalFlow.Device.csproj -- --connect ws://localhost:8000/ws --data ./data-b
```

`plan_ready` / `status` / `proposal` frames print to **stdout**; logs and
offline `agent_event` frames go to **stderr**.

## Environment

Loaded from `.env` in the working directory (plain `KEY=VALUE`) or the process
environment:

| Variable              | Meaning                        | Default                        |
| --------------------- | ------------------------------ | ------------------------------ |
| `OPENROUTER_API_KEY`  | OpenRouter API key             | — (**required**)               |
| `OPENROUTER_BASE_URL` | OpenAI-compatible base URL     | `https://openrouter.ai/api/v1` |
| `OPENROUTER_MODEL`    | Model id                       | `openai/gpt-oss-120b`          |
| `LOG_LEVEL`           | Overrides console log level    | `Information` (`Debug` with `--verbose`) |
| `DEVICE_ID`           | Pairing key sent in `hello`    | persistent self-generated UUID in `<data>/device_id` |
| `DEVICE_NAME`         | Human label in the UI's device picker | `user@machine (<short-id>)` |

## Layout

```
src/GoalFlow.Device/
  Contracts/              C# mirror of every CONTRACT v3 message (snake_case JSON)
  Agent/GoalAgent.cs      the SK kernel host: build kernel, plan, actuate, adapt
  Harness/                THE GENERIC CORE — five first-class components + supporting
                          modules; no product types, no LLM inside:
    CapabilityManager/    the toolbox: plugin/function discovery + the capabilities advertisement
    SafetyPolicyEngine/   the safety gate: an SK IFunctionInvocationFilter over declarative policy.json
    PrecheckEngine/       "is the world ready?" — gates before planning and before each actuation
    TaskManager/          the goal ledger: task DAG, derived progress, the observer/suggester seams
    ProductApiAdapter/    the product seam (IProductApiAdapter)
    Approval/ Grounding/ Clock/ Trace/   supporting steering modules
  Products/FamilyHub/     THE PRODUCT PACK — everything fridge-specific:
    FamilyHubProduct.cs   the manifest: the single plugin + observer catalog
    Plugins/              11 SK plugins — the LLM's tools ([KernelFunction] + [SideEffect] tiers)
    Observers/            the six IDomainObservers (one per domain)
    Probes/ Adapter/ config/   pre-check probes, the mock-world adapter, policy/prechecks JSON
  Transport/WsClient.cs   one outbound BCL ClientWebSocket to the cloud hub
  Program.cs              CLI entry + DI composition root
data/                     mock world (day-offset dates; see data/README.md) + sample contracts
                          + device_id (persisted self-generated pairing key, first run)
docs/                     ARCHITECTURE.md (kernel/filter/stream design), HARNESSES.md (harness catalog)
```

See `CODE_GUIDE.md` for the code walkthrough, `docs/ARCHITECTURE.md` for the
invoke/filter/stream flow, and `docs/HARNESSES.md` for the mapping of the five
harness components (and the supporting modules) to real SK/framework primitives.
