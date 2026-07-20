# Harness Catalog (v3)

**The harness is the reusable, domain-agnostic orchestration layer — the "star."**
v3 makes it explicit: **five first-class components**, each its own folder under
`Harness/`, plus the supporting device-side modules that steer a run. The product
(fridge specifics) lives entirely in `Products/FamilyHub/`; the harness contains zero
product types. This page maps each piece to the REAL primitive it is built on and the
REAL file that implements it — not made-up class names: every type below is one you can
open, and the SK primitives (`[KernelFunction]`, `FunctionChoiceBehavior.Auto`,
`IFunctionInvocationFilter`) are framework features.

Two kinds of thing:

- **capability** — a Semantic Kernel *plugin*: a toolbox of `[KernelFunction]`s the LLM
  calls via auto function-calling. Domain-flavored, and all in the product pack.
- **steering** — deterministic code that shapes/guards the run: it decides what the LLM
  sees, what it may execute, what the user must approve, whether the world is even ready,
  and how the plan survives a changing world. **Steering never contains an LLM** — that
  is the whole point ("LLM plans, code checks").

## The five harness components (v3)

The v3 design elevates five components to first-class folders. They are the generic
core; a second product would reuse all five unchanged.

| # | Component | Folder | Real primitive | What it owns |
|---|---|---|---|---|
| 1 | **Capability Manager** | `Harness/CapabilityManager/` | SK plugin/function discovery (`kernel.Plugins` → `KernelFunctionMetadata` + `[SideEffect]`) | Discovers the toolbox and advertises it (`capabilities` message: modules + per-function side-effect tier + the domain list from the observers). Nothing is hand-listed. |
| 2 | **Safety Policy Engine** | `Harness/SafetyPolicyEngine/` | SK `IFunctionInvocationFilter` + a **declarative** `policy.json` | The hard guardrail. The harness implements rule KINDS (blocked-terms, numeric-cap, time-window, result-screen); the pack's `policy.json` binds them to this product's calls. Grades A0/A1/A2/AX with a **ratchet** (config may only tighten). |
| 3 | **Pre-check Engine** | `Harness/PrecheckEngine/` | Probe interface + config bindings, run at phase boundaries (NOT in the SK filter) | The third gate: *"is this POSSIBLE right now?"* — distinct from safety's *"never"* and approval's *"waiting on a person."* Two gates: before planning (can this goal be planned at all?) and before each actuation (can this effect happen now?). A failed effect DEFERS (the approval stands, runs when the world recovers). |
| 4 | **Task Manager** | `Harness/TaskManager/` | A validated task-DAG ledger + the observer/suggester seams | The goal's ledger: the task DAG, a validated lifecycle, DERIVED progress (`progress_pct`/`pending_tasks`/`next_step` — never inferred from the clock). Also hosts `IDomainObserver` (world-watching + materiality → adaptation) and `ISuggester` (proactive scans), plus `MonitorAdapt`. |
| 5 | **Product API Adapter** | `Harness/ProductApiAdapter/` | `IProductApiAdapter` — the product seam | The one interface between generic harness and a concrete product. `MockFamilyHubAdapter` implements it over `data/*.json`; making the fridge real means one new implementation, no plugin changes. |

## Supporting device-side modules

Not "components" but real steering modules the run needs:

| Module | Real primitive | Where it lives |
|---|---|---|
| **Context Grounding** | Assembler over SK plugins + the generic clock | `Harness/Grounding/Grounding.cs` |
| **Planner (two-altitude)** | SK auto function-calling, LLM-only, in two passes | `Agent/GoalAgent.cs` (`DecomposeAsync` → per-task planning in `RunAsync`) |
| **Approval / Consent (HITL)** | Tiered proposal ledger (auto/light/firm) | `Harness/Approval/ApprovalCoordinator.cs` |
| **Actuator / Effect Executor** | Idempotent executor through the kernel (filter + precheck still apply) | `Agent/GoalAgent.ApplyApprovalAsync` + `ApprovalCoordinator.MarkExecuted` |
| **Scheduler / Temporal** | Generic clock — real or simulated, NEVER hardcoded | `Harness/Clock/Clock.cs` (`IClock`/`SystemClock`/`SimulatedClock`) |
| **Trace / Explain** | `Microsoft.Extensions.Logging` + streamed `agent_event` frames | `Harness/Trace/Trace.cs` |

The cloud owns two more steering roles the device only sees the *output* of: the **Goal
Interpreter** (fuzzy goal → the generic `dispatch` contract, and the M4 actionability
gate that judges a goal against *this device's advertised capabilities*) and **Memory &
Constraints** (emits `constraints.hard`/`soft`; the device consumes, never invents).

## The components, in depth

### 1 · Capability Manager — `Harness/CapabilityManager/CapabilityManager.cs`
The toolbox is *discovered*, not hand-listed: it walks `kernel.Plugins` (KernelFunction
metadata + `[SideEffect]`) plus the fixed steering modules and builds the `capabilities`
message. It also advertises the **domains** the device understands (derived from the
registered `IDomainObserver`s), so the cloud interpreter routes a goal to a domain the
device actually answers to. Add a plugin or an observer and it advertises itself; the
grounding tool set is derived here too (available reads only — a `[Unavailable]` stub is
withheld from the planner). **How it helps:** what the assistant can do is a fact about
the device that is plugged in, discovered, not a hardcoded list.

### 2 · Safety Policy Engine — `Harness/SafetyPolicyEngine/SafetyFilter.cs` (+ `policy.json`)
An `IFunctionInvocationFilter` in the kernel's invocation pipeline — *every* tool call
passes through it BEFORE the plugin runs. It checks the pending call against
`constraints.hard` (its only input) and blocks violations deterministically, never
consulting the LLM. v3 makes it **declarative**: the harness implements rule KINDS, the
pack's `config/policy.json` says which of THIS product's calls they apply to and with
what vocabulary. Grades A0/A1/A2/AX unify the tiers, with a **ratchet** — config may only
make a grade STRICTER; loosening is fatal at startup. Per-goal policy (`AsyncLocal`), so
two concurrent goals never clobber each other's constraints. **How it helps:** the
planner can be creative because code — not the model — enforces
allergens/medical/budget/quiet-hours, and the rules are editable data, not code.

### 3 · Pre-check Engine — `Harness/PrecheckEngine/PrecheckEngine.cs`
The third gate, and the one v2 lacked. Safety asks *"is this ALLOWED?"* (never); approval
asks *"is this CONSENTED?"* (waiting on a person); pre-check asks *"is this POSSIBLE right
now?"* (not yet). Two gates at phase boundaries — before decompose (a goal that can't be
planned, e.g. signed out, shouldn't spend a token) and before each actuation (an oven
online at plan time can be unplugged by the time someone taps Approve). A failed effect
is **deferred**, not failed: the approval stands and re-applying it once the world
recovers executes it (the ledger is idempotent). Probes live in the pack
(`Probes/DeviceStateProbe.cs` + `config/prechecks.json`). **How it helps:** a plan
blocked by REALITY reads as *Waiting* with a fix ("sign in and this resumes"), distinct
from blocked-by-safety and blocked-by-approval.

### 4 · Task Manager — `Harness/TaskManager/TaskManager.cs`
A goal is a **task DAG** with a validated lifecycle, not a flat plan. The manager owns the
ledger and DERIVES the goal-level rollup — `progress_pct`, `pending_tasks`, `next_step` —
from real task state, never from the clock (v2 had no task model, so any progress bar it
showed would have been fiction). Every transition streams a `task_update`, which is how
Agent Board learns what a goal is made of and how far along it is. This folder also hosts
the two product seams: **`IDomainObserver`** (world-watching + materiality → a scoped
adaptation `proposal`; `MonitorAdapt` drives it) and **`ISuggester`** (proactive scans of
local state → `suggestions`). **How it helps:** it turns a one-shot plan into a living,
measurable one — and it is the honest source of every number the board shows.

The Task Manager also enables the **two-altitude planner** (`Agent/GoalAgent.cs`):
altitude one **decomposes** the goal into a task DAG (structure only, no tools; the DAG is
sanitized in code, fail-soft to a single task); altitude two plans each task grounded in
the real world via SK auto function-calling. LLM-only at both altitudes.

### 5 · Product API Adapter — `Harness/ProductApiAdapter/IProductApiAdapter.cs`
The single interface between the generic harness and a concrete product: the clock, and
load/save/reset/offset-resolution over the world documents. `MockFamilyHubAdapter`
implements it over `data/*.json` with the generic-clock rule (dates stored as day
offsets, resolved at read time). **How it helps:** it is the seam that makes the harness
product-agnostic — swap the mock for real Tizen/SmartThings calls by writing one new
implementation; no plugin, no harness code changes.

### Supporting modules, briefly
- **Grounding** anchors the planner to real facts (a small world digest in the prompt +
  live plugin calls for the rest), not hallucinated state.
- **Approval** freezes side-effecting calls into a tiered ledger (auto runs now; light
  rides the plan approval; firm — money/irreversible — never runs without explicit
  consent). The durable pause/resume itself is cloud-side (LangGraph `interrupt()`).
- **Actuator** is the single guarded path from "approved" to "done": it invokes approved
  proposals *through the kernel* (so safety + precheck still apply), idempotently.
- **Clock** makes the whole agent time-relative — "today" is real or simulated (`set_date`
  / `advance_day`), and mock dates are offsets. No date is ever baked into code or data.
- **Trace** is one sink, two audiences: structured logs AND the streamed `agent_event`
  frames the UI renders as "watch it think." `seq` is monotonic per goal.

## Capability plugins (SK plugins — the LLM's tools)

All in `Products/FamilyHub/Plugins/`, registered by the one-line pack manifest
`FamilyHubProduct.AddFamilyHub`, advertised with `kind: "capability"` and per-function
`side_effecting`/`tier` metadata. World access goes through `IProductApiAdapter`.

**11 plugins, all implemented** (the three v2 stubs were implemented in M7; `SecurityPlugin`
was added in M7).

| Plugin (module) | Domain | [KernelFunction]s (side-effecting → tier) |
|---|---|---|
| `InventoryPlugin` (Inventory) | meal | `ListItems`, `GetExpiringItems`, `CheckAvailability`, `ConsumeItem` → auto |
| `CalendarPlugin` (Calendar) | shared | `GetEvents`, `GetBusyEvenings`, `AddEvent` → light |
| `RecipePlugin` (Recipes) | meal | `FindRecipes`, `GetRecipe` |
| `ShoppingListPlugin` (ShoppingList) | shared | `GetList`, `Add` → light, `Remove` → light, `PlaceOrder` → **firm** (spends money, budget-capped) |
| `ReminderPlugin` (Reminders) | shared | `List`, `Create` → auto, `Delete` → auto |
| `GuestsPlugin` (Guests) | guest_dinner | `GetEvent`, `GetGuests`, `GetDietaryConstraints` |
| `ApplianceControlPlugin` (Appliance) | shared (SmartThings) | `ListAppliances`, `PreheatOven` → light, `RunProgram` → light, `Defrost` → auto |
| `SecurityPlugin` (Security) | vacation | `GetSecurityStatus`, `LockAllDoors` → auto, `ArmSecurity` → light (camera prechecks) |
| `FamilyProfilesPlugin` (FamilyProfiles) | shared | `GetProfiles`, `GetMember` |
| `BudgetPlugin` (Budget) | shared | `GetBudgetStatus`, `EstimateCost` (the cap itself is enforced by the Safety Policy Engine) |
| `NotifyPlugin` (Notify) | shared | `SendNotification` → auto, `Announce` → light (quiet-hours checked) |

## Domain observers, probes, suggester (the product's judgement)

- **Observers** (`Products/FamilyHub/Observers/`): `MealPlanObserver`, `GuestDinnerObserver`,
  `VacationPrepObserver`, `BirthdayPartyObserver`, and (v3.4) `GroceryCostObserver` +
  `EnergySavingObserver` — one per domain, six in all. Registering one IS the domain
  advertisement the cloud routes on; each captures its slice of the world and decides which
  changes are material (adaptation).
- **Probes** (`Products/FamilyHub/Probes/`): `DeviceStateProbe` + `ApplianceOnlineProbe` —
  the runtime conditions this product's calls need (Samsung account, SmartThings, camera).
- **Suggester** (`Products/FamilyHub/InventorySuggester.cs`): a deterministic scan of local
  state → proactive `suggestions` (Expiring Soon, Grocery Restock) the board offers.

**Generality in one line:** six domains (meal, guest, vacation, birthday, grocery_cost,
energy_saving) run on the same five components and the same protocol — a domain is an
`IDomainObserver` plus the plugins
it uses, and the cloud accepts it purely because the device advertises it. Adding a fifth
domain changes no harness code.
