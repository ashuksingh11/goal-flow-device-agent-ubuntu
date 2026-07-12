# GoalFlow Device Agent (Ubuntu / .NET 8) — v2

The **on-device agent** for GoalFlow — the **executor tier of a general
goal agent** for the Samsung Family Hub. The cloud agent owns the conversation
and memory and decomposes a fuzzy user goal into a generic **Task Contract**
(the `dispatch` frame); this device agent is the sole authority on local state
and the only component that touches actuators.

Ethos: *"fake the world; make the mechanism real."* The world is mock JSON;
the agent mechanics — SK auto function-calling, a deterministic safety filter,
tiered approvals, a sustain loop — are real.

**v2 core idea: the device IS a Semantic Kernel agent.** There is no
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
- **General agent, not a meal app.** Two domains ship — `meal_plan` and
  `guest_dinner` — running through the *same* kernel host, steering modules,
  and protocol. Domains differ only in which capability plugins the planner
  leans on; adding a domain = registering plugins.
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

```bash
dotnet build GoalFlow.Device.sln

# One-shot plan from a natural-language goal (local dispatch is synthesized):
dotnet run -- --goal "help us eat healthier this week" [--domain meal_plan]

# One-shot plan from a Task Contract file (plan_ready JSON on stdout):
dotnet run -- --contract data/sample-contract.json
dotnet run -- --contract data/sample-contract-guest.json

# ... optionally apply (and replay) an approval afterwards:
dotnet run -- --contract data/sample-contract.json --approval data/sample-approval.json

# Live session: connect the outbound WebSocket to a running cloud hub:
dotnet run -- --connect ws://localhost:8787/ws

# Headless sustain demos (plan, then advance days; adaptation on the material day).
# Both run on a temp copy of data/ so the seed world is never dirtied:
dotnet run -- --simulate-week    # meal_plan: 5 weekday ticks
dotnet run -- --simulate-guest   # guest_dinner: 2 ticks to the RSVP/late-arrival trigger

# Extras: [--date 2026-07-14] start a SimulatedClock there; [--data ./data]; [--verbose]
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

## Layout

```
src/GoalFlow.Device/
  Contracts/              C# mirror of every CONTRACT v2 message (snake_case JSON)
  Agent/GoalAgent.cs      the SK kernel host: build kernel, plan, actuate, adapt
  Modules/
    Capabilities/         SK plugins — the LLM's tools ([KernelFunction] + [SideEffect] tiers)
    Steering/             deterministic harness modules (safety, approvals, grounding,
                          clock, monitor/adapt, trace, capability registry)
  Transport/WsClient.cs   one outbound BCL ClientWebSocket to the cloud hub
  Program.cs              CLI entry + DI composition root
data/                     mock world (day-offset dates; see data/README.md) + sample contracts
docs/                     ARCHITECTURE.md (kernel/filter/stream design), HARNESSES.md (module catalog)
```

See `CODE_GUIDE.md` for the code walkthrough, `docs/ARCHITECTURE.md` for the
invoke/filter/stream flow, and `docs/HARNESSES.md` for the mapping of the 11
harness modules to real SK/framework primitives.
