# Harness Catalog (v2)

**Count:** **11 harness modules** (the reusable, domain-agnostic orchestration
layer — the "star") + **10 capability plugins** (the SK tools the LLM calls).
The two are different categories: harness modules *steer/orchestrate*; capability
plugins are the *toolbox*. Of the 11 harness modules, 2 are cloud-side (Goal
Interpreter, Memory & Constraints) and 9 are device-side. Of the 10 capability
plugins, 7 are implemented and 3 (FamilyProfiles, Budget, Notify) are named stubs.

The reusable star of GoalFlow is the set of **11 domain-agnostic harness
modules** from the v2 design proposal. This page maps each one to the REAL
primitive it is built on and the REAL file that implements it in this repo —
these are not made-up class names: every device-side module below is a
concrete type you can open, and the SK primitives (`[KernelFunction]`,
`FunctionChoiceBehavior.Auto`, `IFunctionInvocationFilter`) are framework
features, not conventions.

Two kinds of module:

- **capability** — a Semantic Kernel *plugin*: a toolbox of `[KernelFunction]`s
  the LLM calls via auto function-calling. Domain-flavored (Inventory is meal;
  Calendar is shared).
- **steering** — deterministic code that shapes/guards the run: it decides
  what the LLM sees, what it may execute, and what the user must approve.
  Steering modules never contain an LLM.

## The 11 harness modules → real primitives

| # | Harness module | Kind | Real primitive | Where it lives |
|---|---|---|---|---|
| 1 | **Goal Interpreter** | steering (cloud) | LangGraph node + structured output | Cloud repo — the fuzzy goal becomes the generic `dispatch` Task Contract before it ever reaches the device |
| 2 | **Memory & Constraints** | steering (cloud) | Store + verbatim injection (hard) / retrieval (soft) | Cloud repo — emits `constraints.hard`/`soft` on the dispatch; device consumes, never invents |
| 3 | **Context Grounding** | steering (device) | Assembler over SK plugins + the generic clock | `Harness/Grounding/Grounding.cs` (`Grounding`, `GroundingContext`) |
| 4 | **Capability Registry** | steering (device) | SK plugin/function discovery (`kernel.Plugins` → `KernelFunctionMetadata` + `[SideEffect]`) | `Harness/CapabilityManager/CapabilityManager.cs` → the `capabilities` message |
| 5 | **Planner** | device kernel host | **SK auto function-calling** (`FunctionChoiceBehavior.Auto` over the capability plugins), LLM-only | `Agent/GoalAgent.cs` (`RunAsync`) |
| 6 | **Safety & Policy Filter** | steering (device) | **SK `IFunctionInvocationFilter`** — vetoes pending calls vs `constraints.hard`; "LLM plans, code checks" | `Harness/SafetyPolicyEngine/SafetyFilter.cs` |
| 7 | **Approval / Consent (HITL)** | steering (split) | Device: tiered proposal ledger. Cloud: LangGraph `interrupt()` holds the durable pause | `Harness/Approval/ApprovalCoordinator.cs` (auto/light/firm; pending → approved → executed) |
| 8 | **Actuator / Effect Executor** | steering (device) | Idempotent executor invoking approved proposals through the kernel (filter still applies) | `Agent/GoalAgent.cs` (`ApplyApprovalAsync`) + `ApprovalCoordinator.MarkExecuted` |
| 9 | **Scheduler / Temporal** | steering (device) | Generic clock abstraction — real or simulated, NEVER hardcoded | `Harness/Clock/Clock.cs` (`IClock`, `SystemClock`, `SimulatedClock`) |
| 10 | **Monitor & Adapt** | steering (device) | Change watcher + deterministic materiality policy → scoped adaptation `proposal` | `Harness/TaskManager/MonitorAdapt.cs` (`MonitorAdapt`, `MaterialityPolicy`, `WorldChange`) |
| 11 | **Trace / Explain** | steering (device) | `Microsoft.Extensions.Logging` (structured, correlation-scoped) + streamed `agent_event` frames | `Harness/Trace/Trace.cs` |

Goal-side modules (#1, #2) are cloud-owned; the device sees their *output* in
the dispatch. Everything else is a real class in this repo, advertised to the
cloud/UI in the `capabilities` message (`kind: "steering"` entries come from
`CapabilityManager.SteeringModules`).

## Device-side harness modules, in depth — what each is & how it helps

These are the **9 device-side harness modules** (#3–#11). They are the reusable,
domain-agnostic orchestration layer: they don't *do* the domain work (that's the
capability plugins), they **steer** it — deciding what the LLM sees, what it may
run, what the user must approve, and how the plan survives a changing world. None
of them contains an LLM; that is the point ("LLM plans, code checks").

### 3 · Context Grounding — `Harness/Grounding/Grounding.cs`
**What it is:** an assembler that builds the planner's opening context from the
*real* world in two halves — a pre-pass snapshot (the clock's date, the time
window resolved against it, `constraints.hard`/`soft` verbatim, and a short world
digest) rendered into the system prompt, plus the live half: the capability
plugins the model calls mid-plan for anything the digest didn't cover.
**How it helps:** it anchors the LLM to facts instead of hallucinated state, so
the plan is about *this* family's real inventory, calendar and constraints — and
keeps the opening prompt small (a digest, not a data dump) while leaving the
details a function call away.

### 4 · Capability Registry — `Harness/CapabilityManager/CapabilityManager.cs`
**What it is:** the toolbox is *discovered*, not hand-listed — this walks
`kernel.Plugins` (KernelFunction metadata + the `[SideEffect]` attribute) plus the
fixed steering modules and builds the `capabilities` message sent right after
`hello_ack`. **How it helps:** the cloud/UI learn what the device can do (and each
function's side-effecting/tier metadata) without hard-coding it; add a plugin and
it advertises itself. It's also the source of truth the SafetyFilter and
ApprovalCoordinator read to decide which calls must be frozen into proposals.

### 5 · Planner — `Agent/GoalAgent.RunAsync` (SK auto function-calling)
**What it is:** not a bespoke planner class but the SK kernel host running
`FunctionChoiceBehavior.Auto` over the capability plugins — the LLM decides which
tools to call, in what order, to satisfy the objective; LLM-only, no rules/scripted
fallback. **How it helps:** one generic mechanism plans *any* domain (meal, guest
dinner, …) by composing whatever plugins the goal needs — generality comes from
the toolbox, not from planner code.

### 6 · Safety & Policy Filter — `Harness/SafetyPolicyEngine/SafetyFilter.cs`
**What it is:** a Semantic Kernel `IFunctionInvocationFilter` in the kernel's
invocation pipeline — *every* tool call the LLM makes passes through it BEFORE the
plugin method runs. It checks the pending call against `constraints.hard` (its only
input) and blocks violations deterministically, never consulting the LLM. **How it
helps:** this is the hard guardrail behind "LLM plans, code checks" — a blocked call
doesn't run; instead the model gets a structured refusal (so it re-plans) and the
violation is recorded for the `plan_ready` safety verdict. The planner can be
creative because code, not the model, enforces allergens/medical/budget/quiet-hours.

### 7 · Approval / Consent (HITL) — `Harness/Approval/ApprovalCoordinator.cs`
**What it is:** the device half of human-in-the-loop — the proposal **ledger**.
Side-effecting calls the LLM proposes are frozen into `ProposalItem`s by tier:
`auto` may execute immediately, `light` rides the plan approval, `firm` (spends
money / irreversible) NEVER executes until an explicit approval arrives. Lifecycle:
pending → approved → executed (or rejected). **How it helps:** it gives the user
proportionate control — trivial actions don't nag, consequential ones are gated —
and guarantees nothing irreversible happens without consent. (The durable
pause/resume itself lives cloud-side in LangGraph `interrupt()`.)

### 8 · Actuator / Effect Executor — `Agent/GoalAgent.ApplyApprovalAsync` (+ `ApprovalCoordinator.MarkExecuted`)
**What it is:** when an approval frame arrives it flips the ledger decisions and
invokes the approved proposals *through the kernel* (so the SafetyFilter still
applies), idempotently. **How it helps:** it's the single, guarded path from
"approved" to "actually done" — re-applying the same approval is a no-op (safe on
reconnect/replay), and the resulting `status.executed` is what the UI turns into
"5 items added ✓".

### 9 · Scheduler / Temporal — `Harness/Clock/Clock.cs` (`IClock`/`SystemClock`/`SimulatedClock`)
**What it is:** the generic clock every other module reads. INVARIANT: nothing ever
hardcodes a date — "today" is the real system date or a simulated date driven by
`control` frames (`set_date` / `advance_day`); mock-world dates are stored as day
OFFSETS and resolved against it. **How it helps:** it makes the whole agent
time-relative, so a demo runs on any calendar date and the presenter can advance
days to trigger adaptations — without a single date baked into code or data.

### 10 · Monitor & Adapt — `Harness/TaskManager/MonitorAdapt.cs` (+ `MaterialityPolicy`)
**What it is:** after a plan is approved the world keeps moving (a day passes, an
item expires early, a guest RSVPs an allergy). This compares fresh world state
against the plan's assumptions, applies the **materiality policy** — deterministic
code, not the LLM, decides whether a change *matters* — and when it does, produces
a scoped adaptation `proposal` that re-plans ONLY the affected slice. **How it
helps:** it turns a one-shot plan into a living one that reacts to reality, while
the materiality gate prevents noise (4 quiet days, 1 smart adaptation) and the
scoped re-plan keeps the change cheap and legible.

### 11 · Trace / Explain — `Harness/Trace/Trace.cs`
**What it is:** one sink, two audiences — structured logs via
`Microsoft.Extensions.Logging` (leveled, correlation-id-scoped) AND streamed
`agent_event` frames over the WebSocket (phase / thinking / tool_call / tool_result
/ plan_progress). Every emit does both; `seq` is monotonic per goal. **How it
helps:** it makes the agent debuggable *and* demoable from the same events — the
logs explain a run after the fact, the stream powers the live "watch it think"
feed the UI renders.

## Capability modules (SK plugins — the LLM's tools)

All in `Products/FamilyHub/Plugins/`, registered in `GoalAgent.BuildKernel`,
advertised with `kind: "capability"` and per-function
`side_effecting`/`tier` metadata. Mock-world access goes through
`MockFamilyHubAdapter` (relative dates; see `data/README.md`).

**10 capability plugins** (7 implemented + 3 stubs). `MockFamilyHubAdapter` is shared
infra, not a plugin.

| Plugin (module name) | Domain | Status | [KernelFunction]s (side-effecting → tier) |
|---|---|---|---|
| `InventoryPlugin` (Inventory) | meal | ✅ | `ListItems`, `GetExpiringItems`, `CheckAvailability`, `ConsumeItem`, `MarkConsumed` → auto |
| `CalendarPlugin` (Calendar) | shared | ✅ | `GetEvents`, `GetBusyEvenings`, `AddEvent` → light |
| `RecipePlugin` (Recipes) | meal | ✅ | `FindRecipes`, `GetRecipe` |
| `ShoppingListPlugin` (ShoppingList) | shared | ✅ | `GetList`, `Add` → light, `Remove` → light, `PlaceOrder` → **firm** (spends money) |
| `ReminderPlugin` (Reminders) | shared | ✅ | `List`, `Create` → auto, `Delete` → auto |
| `GuestsPlugin` (Guests) | guest_dinner | ✅ | `GetEvent`, `GetGuests`, `GetDietaryConstraints` (read-only; RSVPs + merged allergy/diet constraints) |
| `ApplianceControlPlugin` (Appliance) | shared (SmartThings) | ✅ | `ListAppliances`, `PreheatOven` → light, `RunProgram` → light, `Defrost` → auto |
| `FamilyProfilesPlugin` (FamilyProfiles) | shared | 🚧 stub | `GetProfiles`, `GetMember` *(extension point — NotImplementedException)* |
| `BudgetPlugin` (Budget) | shared | 🚧 stub | `GetBudgetStatus`, `EstimateCost` *(extension point; the cap is enforced by SafetyFilter regardless)* |
| `NotifyPlugin` (Notify) | shared | 🚧 stub | `SendNotification` → auto, `Announce` → light *(extension point)* |

**Generality in one line:** the `guest_dinner` domain (built in v2-M4) adds the
`GuestsPlugin` and *reuses* Calendar, Reminders, Appliance, ShoppingList and
Recipes — same 11 steering/harness modules, same protocol, a different toolbox
subset. Not every goal uses every plugin; the toolbox is composed from the
registry per goal. Adding a third domain = add its capability plugin(s); the 11
harness modules don't change.
