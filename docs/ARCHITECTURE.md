# Device Agent Architecture

The GoalFlow device agent is the on-device half of a two-tier goal-agent
system. The cloud agent owns the conversation and memory, decomposes the user
goal into a **Task Contract** (the `dispatch` message of CONTRACT v0 — the
canonical CONTRACT.md lives in the cloud repo; `src/GoalFlow.Device/Contracts/`
is its exact C# mirror), and relays messages. The device agent:

- is the **sole authority on local state** — nothing else reads or mutates
  the local world;
- is the **only component that touches actuators** (shopping list,
  reminders);
- runs the **harness pipeline** described below.

The UI and the device never talk directly. The cloud is the WebSocket hub;
the device opens one outbound `ClientWebSocket` to it. Ethos of the POC:
*"fake the world; make the mechanism real"* — the product APIs are mock JSON,
but the pipeline, gates, contracts, and correlation/idempotency machinery are
the real IP.

## The harness pipeline

Order: **sense → decide → gate → act → sustain**, with Trace cross-cutting.
`Pipeline.cs` is the orchestrator; each harness is one interface + one class
in `Harnesses/`.

| Phase | Harnesses |
| --- | --- |
| orchestrate | TaskManager (goal_id, status lifecycle) |
| sense | PreCheck → CapabilityManager → Grounding (over the product API adapters) |
| decide | Planner (swappable IPlanner) |
| gate | SafetyGate (code, blocks) then ApprovalBroker (user via cloud, waits) |
| act | EffectExecutor (idempotent, dedupe on correlation_id) |
| sustain | Scheduler + ChangeWatcher (M4) |
| cross-cutting | Trace, IClock |

Lifecycle driven by TaskManager:
`created → planning → awaiting_approval → executing → adapting → done`.

Grounding is the world-state assembler: it normalizes the adapters' outputs
(inventory, calendar, recipes, shopping list, reminders) into one coherent
`WorldState`, including derived views such as `ExpiringSoon` (the
reduce_waste signal). The planner and gates only ever see `WorldState` —
never raw adapter payloads.

## The two-gate separation

This is the load-bearing design decision, kept as **two separate classes**:

1. **Safety gate (`ISafetyGate` / `SafetyGate`)** — deterministic CODE. Its
   only inputs are the candidate plan, `constraints.hard` from the contract,
   and recipe facts from the world state. It checks allergens / dietary /
   medical rules, and on violation it **blocks** and reports
   `hard_violations`. No LLM, no network, no repair — block, don't fix.
2. **Approval gate (`IApprovalBroker` / `ApprovalBroker`)** — the human,
   reached through the cloud. It freezes side-effects as **proposals**,
   tracks pending/approved/rejected/expired, and **waits** for `approval`
   messages, correlating each decision by `correlation_id`.

The planner never runs the safety check. Even the LlmPlanner's output is
untrusted and passes through the same code gate. Slogan: **"LLM plans, code
checks."** Corollary invariant: the device sends side-effects only as
proposals and executes nothing until an approval returns; execution then
happens exactly once (EffectExecutor dedupes on correlation_id + proposal_id).

## Swappable planner

`IPlanner` has three implementations, selected via DI/config (`--planner`):

- **RulesPlanner** (default) — deterministic heuristics: consume
  expiring-soon inventory first, honor soft preferences/dislikes, pick quick
  recipes on busy calendar evenings, diff ingredients vs inventory into one
  shopping-list proposal. Demo-safe: no network, no keys.
- **LlmPlanner** — Microsoft Semantic Kernel over OpenRouter
  (OpenAI-compatible; `OPENROUTER_BASE_URL`, default model
  `anthropic/claude-sonnet-5` via `OPENROUTER_MODEL`). Falls back on any
  failure.
- **ScriptedPlanner** — canned fixture replay (M1 smoke runs, offline demos).

All three return the same `CandidatePlan` and are gated identically.

## Portability: injectable interfaces + virtual clock

Everything Tizen-specific sits behind injectable interfaces so the Tizen port
(goal-flow-device-agent-tizen) is an adapter swap, not a rewrite:

- **Product/device APIs** — `IInventoryApi`, `ICalendarApi`, `IRecipeApi`,
  `IShoppingListApi`, `IReminderApi`. Ubuntu uses `Mock*` classes over
  `data/*.json`; Tizen swaps in Family Hub adapters.
- **Local storage** — the mock adapters own file I/O; a Tizen adapter uses
  platform storage behind the same interfaces.
- **Clock** — the `IClock` discipline: device code never calls
  `DateTime.Now`/wall-clock timers. Everything (Grounding timestamps,
  Scheduler waits, proposal expiry) reads the virtual clock, so demos can
  time-travel and tests are deterministic. `VirtualClock` anchors at
  2026-07-12 to match the seed data.

All harnesses are constructor-injected (`Microsoft.Extensions.DependencyInjection`);
`Program.cs` documents the intended composition root.

## Transport vs pipeline (M1 vs later)

The pipeline is transport-agnostic. Milestone 1 drives it from the command
line (`dotnet run -- --contract data/sample-contract.json` prints a
`plan_ready` JSON; compare with `data/golden-plan_ready.json`). The
WebSocket shell (`Transport/WsClient.cs`) is a later thin wrapper:
deserialize `dispatch` → `Pipeline.RunAsync` → serialize `plan_ready`;
deserialize `approval` → `Pipeline.OnApprovalAsync` → serialize `status`.
It reconnects on drop and relies on correlation_id dedupe for at-least-once
delivery. Switching from Linux demo fallback to the real Family Hub is a
one-line endpoint swap.

## Named extension points (deliberately not designed yet)

- Budget / quiet-hours harness (a third policy gate slot after SafetyGate).
- Graceful-degradation harness (behavior when adapters or the LLM are down
  beyond the current planner fallback chain).
- Medical rule table in SafetyGate (slot exists; POC data keeps it empty).
