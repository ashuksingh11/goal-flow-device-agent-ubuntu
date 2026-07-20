# Device Agent Architecture (v3 — Semantic Kernel)

The GoalFlow device agent is the on-device half of a two-tier goal-agent
system for the Samsung Family Hub. The cloud agent owns the conversation and
memory, decomposes the fuzzy user goal into the **generic Task Contract**
(the `dispatch` message of CONTRACT v3 — canonical in the cloud repo;
`src/GoalFlow.Device/Contracts/` is its exact C# mirror), and relays every
message (UI and device never talk directly).

**Core idea: the device IS a Semantic Kernel agent.** Device capabilities
are SK **plugins** whose methods are `[KernelFunction]`s the LLM *calls* via
**auto function-calling** (`FunctionChoiceBehavior.Auto`). Safety is an SK
**`IFunctionInvocationFilter`** that inspects every pending call against
`constraints.hard` and blocks violations deterministically — **"LLM plans,
code checks."** As the kernel works, the device **streams `agent_event`
frames** (phase / thinking / tool_call / tool_result / plan_progress) that
drive the live UI. Planning is **LLM-only**: there is no rules or scripted
planner.

The agent is **general**: six domains ship — `meal_plan`, `guest_dinner`,
`vacation_prep`, `birthday_party`, `grocery_cost` and `energy_saving`. The
protocol, the harness components, and the kernel host are domain-agnostic;
domains differ only in which capability plugins the planner leans on, which
`IDomainObserver` watches them, and what rides in the free-form
`scope`/`context` objects.

## Layout

```
src/GoalFlow.Device/
  Contracts/              C# mirror of every CONTRACT v3 message (snake_case JSON)
  Agent/GoalAgent.cs      the SK kernel host: build kernel, plan, actuate, adapt
  Harness/                THE GENERIC CORE — five first-class components
                          (CapabilityManager, SafetyPolicyEngine, PrecheckEngine,
                          TaskManager, ProductApiAdapter) + supporting modules
                          (Approval, Grounding, Clock, Trace); no product types, no LLM
  Products/FamilyHub/     THE PRODUCT PACK — 11 plugins (the LLM's tools), the six
                          IDomainObservers, pre-check probes, the mock-world adapter, config
  Transport/WsClient.cs   one outbound BCL ClientWebSocket to the cloud hub
  Program.cs              CLI entry: --goal | --contract | --connect [--date]
data/                     mock world; ALL dates stored as offsets from today
```

## The Kernel (Agent/GoalAgent.cs)

`GoalAgent.BuildKernel` assembles the device's brainstem:

1. **Chat model** — OpenRouter via SK's OpenAI-compatible connector:
   `AddOpenAIChatCompletion(modelId: OPENROUTER_MODEL /* default
   openai/gpt-oss-120b */, endpoint: OPENROUTER_BASE_URL, apiKey:
   OPENROUTER_API_KEY)`. Config comes from `.env`; never hardcoded.
2. **Capability plugins** — the 11 SK plugins registered under their advertised
   module names: `Inventory`, `Calendar`, `Recipes`, `ShoppingList`,
   `Reminders`, `Guests`, `Appliance`, `FamilyProfiles`, `Budget`, `Notify`,
   `Security`. Registration IS the action space: the planner can only call what
   the Hub can actually do.
3. **The SafetyFilter** — added as an `IFunctionInvocationFilter` service, so
   it sits in the kernel's invocation pipeline for *every* function call.

`GoalAgent.RunAsync(dispatch)` is **not** one chat invocation — the planner has
**two altitudes**, and the split is deliberate (a toolless model asked about the
fridge would invent facts; a tool-happy model asked to plan would half-execute):

1. **decompose** — a JSON-mode call with **no tools** asks only for the goal's
   structure (task titles + dependencies). `TaskDag.Sanitize` repairs the result
   and falls soft to a single task, so a decomposition problem never costs the
   user their goal.
2. **grounding** — a **streaming** chat call with
   `FunctionChoiceBehavior.Auto()` scoped to the **read-only** function subset
   (`CapabilityManager.GetGroundingFunctions` — every non-side-effecting function
   of every available plugin, in the pack's order). The model grounds itself by
   *calling* those `[KernelFunction]`s; the kernel invokes them through the
   filter and the results feed back.
3. **compose** — a second, **no-tools** call with `ResponseFormat =
   "json_object"` returns the final plan as one structured JSON object (plan +
   proposals). Side effects are deliberately *not* tools here: the model must
   *propose* mutations naming real side-effecting functions. The compose prompt
   injects only the **active domain's** `PlanShapeRule`, and `AssignPlanDays`
   then stamps each item's `Day` afterward.

Throughout, `Trace` narrates each step (`phase` / `thinking` / `tool_call` /
`tool_result` / `plan_progress`) as an `agent_event` frame.

## Invoke / filter / stream flow

```mermaid
sequenceDiagram
    participant Cloud
    participant WsClient
    participant GoalAgent as GoalAgent (SK Kernel)
    participant LLM as OpenRouter LLM
    participant Filter as SafetyFilter (IFunctionInvocationFilter)
    participant Plugin as Capability plugin ([KernelFunction])
    participant Trace

    Cloud->>WsClient: dispatch (Task Contract)
    WsClient->>GoalAgent: RunAsync(dispatch)
    Note over GoalAgent,LLM: ALTITUDE 1 — decompose (no tools, JSON)
    GoalAgent->>LLM: decompose: goal → tasks + deps
    LLM-->>GoalAgent: task list (TaskDag.Sanitize; falls soft to 1)
    GoalAgent->>Trace: phase: grounding
    Trace-->>Cloud: agent_event (streamed)
    Note over GoalAgent: Grounding assembles world context;<br/>SafetyFilter.BeginGoal(goal_id, constraints.hard)
    Note over GoalAgent,Plugin: ALTITUDE 2a — grounding (streaming, read-only tools)
    GoalAgent->>LLM: chat + READ tool schemas (FunctionChoiceBehavior.Auto)
    loop auto function-calling (read-only)
        LLM-->>GoalAgent: function call {module, function, args}
        GoalAgent->>Trace: tool_call event
        Trace-->>Cloud: agent_event
        GoalAgent->>Filter: OnFunctionInvocationAsync(context)
        alt violates constraints.hard
            Filter-->>GoalAgent: BLOCKED (result = refusal; plugin never runs)
            Note over Filter: violation recorded for the<br/>plan_ready safety verdict
        else allowed
            Filter->>Plugin: next(context) — read executes
            Plugin-->>GoalAgent: result
        end
        GoalAgent->>Trace: tool_result event
        Trace-->>Cloud: agent_event
        GoalAgent->>LLM: tool result appended
    end
    GoalAgent->>Trace: phase: planning
    Note over GoalAgent,LLM: ALTITUDE 2b — compose (NO tools, JSON; active domain's PlanShapeRule)
    GoalAgent->>LLM: compose: final plan + proposals as one JSON object
    LLM-->>GoalAgent: structured plan (plan + proposals)
    Note over GoalAgent: AssignPlanDays stamps each item's Day
    GoalAgent->>Trace: phase: checking
    Note over GoalAgent: side-effecting calls frozen into<br/>tiered proposals (ApprovalCoordinator)
    GoalAgent->>WsClient: plan_ready (plan + proposals + safety verdict)
    WsClient->>Cloud: plan_ready → present_plan → UI
    Cloud->>WsClient: approval (decisions)
    WsClient->>GoalAgent: ApplyApprovalAsync
    Note over GoalAgent: Actuator invokes approved proposals<br/>through the kernel (filter still applies), idempotently
    GoalAgent->>WsClient: status (executed ids)
```

## The Safety filter — "LLM plans, code checks"

`Harness/SafetyPolicyEngine/SafetyFilter.cs` implements SK's
`IFunctionInvocationFilter`. Before *any* plugin method runs, the kernel calls
`OnFunctionInvocationAsync(context, next)`; the filter inspects
`context.Function` (plugin + function name) and `context.Arguments` against
the **armed policy** — the dispatch's `constraints.hard` object, its ONLY
input. Allowed → `next(context)` executes the function. Violation → `next` is
never called: the plugin method does not run, `context.Result` is set to a
structured refusal (so the model sees why and can re-plan), and the violation
is recorded into the `plan_ready` safety verdict (`gate: blocked`).

The filter is deterministic code, fully separate from the LLM. It understands
the hard-constraint vocabulary — `allergens`/`dietary`/`medical` (ingredient
screens), `budget_cap` (blocks `ShoppingList.PlaceOrder` over the cap),
`quiet_hours` (blocks scheduled appliance/announce actions) — and ignores
keys it does not know rather than guessing.

## Approval tiers (HITL)

Side-effecting `[KernelFunction]`s are tagged `[SideEffect(tier)]`
(`Harness/CapabilityManager/CapabilityManager.cs`). Tiers encode
reversibility × cost × risk:

- **auto** — reversible/cheap, may execute immediately (create a reminder);
- **light** — batched into the plan approval (add to shopping list);
- **firm** — spends money / irreversible; NEVER executes before an explicit
  `approval` decision (place the grocery order).

`ApprovalCoordinator` owns the ledger: pending → approved → executed (or
rejected), idempotent execution via `MarkExecuted`.

## The generic clock

`Harness/Clock/Clock.cs`: `IClock` (`Now`, `Today`) with two
implementations — `SystemClock` (real date; the default) and `SimulatedClock`
(starts at real today or `--date`; driven by `control` frames: `set_date`,
`advance_day`). **Nothing hardcodes a date.** Mock data stores day *offsets*
(`expires_in_days`, `day_offset`) that `MockFamilyHubAdapter` resolves against
`IClock.Today` at read time, so the seed world is always "this week"
(see `data/README.md`).

## The event-driven meal demo (`control: trigger_event`)

Alongside the clock-driven sustain loop, `plan_ready.payload.demo_events`
(built from `data/daily_events.json`) advertises a small catalog of
presenter-fired events for the `meal_plan` domain (a restock, a spoiled
ingredient, a calendar clash, a guest, an unavailable appliance, a
"lighter dinner" request), each tied to a plan item's `day`. Firing one sends
`control: trigger_event { event_id }`; `GoalAgent.HandleControlCoreAsync`
handles it *before* any clock stepping (the clock stays frozen for this
path), looks up the event, checks it hasn't already fired, and — if material —
runs one scoped LLM re-plan against just that event's `context` + `steer`,
returning a minimal `PlanPatch` as an adaptation `proposal` through the same
approval → actuation path as the sustain loop.

## Capability registry & the capabilities message

`CapabilityManager` builds the `capabilities` advertisement by *discovery*:
it walks `kernel.Plugins` (function names + `Description` attributes from
`KernelFunctionMetadata`, `side_effecting`/`tier` from `[SideEffect]`), appends
the fixed steering-module descriptors, and lists the domains from the registered
`IDomainObserver`s (`Domain` + `Hint`). Sent right after `hello_ack`. Adding a
domain = registering an `IDomainObserver` (and whatever plugins it needs);
advertisement, planner action space, the domain list, and the UI module view all
update automatically.

## agent_event streaming & structured logging

`Harness/Trace/Trace.cs` is the single narration sink with two audiences:

- **`agent_event` frames** streamed over the WebSocket (monotonic `seq` per
  goal): `phase`, `thinking` (streamed model text), `tool_call`,
  `tool_result`, `plan_progress` — the "watch it think" UI feed;
- **structured logs** via `Microsoft.Extensions.Logging` (console sink):
  leveled, single-line, timestamped, with goal_id/correlation_id scopes —
  the debugging feed.

Every emit does both, so the presenter view and the log tail always agree.

## Transport

`Transport/WsClient.cs` — one outbound BCL `ClientWebSocket` to the cloud hub
(no transport packages). `hello(role: device)` → `hello_ack` →
`capabilities`, then full-duplex: inbound `dispatch`/`approval`/`control`
routed to `GoalAgent`; outbound `plan_ready`/`proposal`/`status` plus the
high-rate `agent_event` stream (writes serialized on a semaphore). Dedupe on
`correlation_id`; reconnect with backoff.

## Tizen-lean dependency rule (hard)

This process must port to Tizen.NET on the Family Hub. The ONLY NuGet
dependencies allowed:

| Package | Why |
|---|---|
| `Microsoft.SemanticKernel` | kernel, plugins, filters, OpenAI-compatible connector |
| `Microsoft.Extensions.Logging` (+ `.Console`) | structured logging |
| `Microsoft.Extensions.DependencyInjection` | composition root |

Everything else is BCL — `System.Text.Json` and `System.Net.WebSockets` ship
in-box on net8.0. **No other, native, or desktop packages.** The csproj
enforces and documents this; adding anything else breaks the port.
