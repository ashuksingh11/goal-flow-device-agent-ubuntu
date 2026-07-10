# Harness Catalog (v2)

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
| 3 | **Context Grounding** | steering (device) | Assembler over SK plugins + the generic clock | `Modules/Steering/Grounding.cs` (`Grounding`, `GroundingContext`) |
| 4 | **Capability Registry** | steering (device) | SK plugin/function discovery (`kernel.Plugins` → `KernelFunctionMetadata` + `[SideEffect]`) | `Modules/Steering/CapabilityRegistry.cs` → the `capabilities` message |
| 5 | **Planner** | device kernel host | **SK auto function-calling** (`FunctionChoiceBehavior.Auto` over the capability plugins), LLM-only | `Agent/GoalAgent.cs` (`RunAsync`) |
| 6 | **Safety & Policy Filter** | steering (device) | **SK `IFunctionInvocationFilter`** — vetoes pending calls vs `constraints.hard`; "LLM plans, code checks" | `Modules/Steering/SafetyFilter.cs` |
| 7 | **Approval / Consent (HITL)** | steering (split) | Device: tiered proposal ledger. Cloud: LangGraph `interrupt()` holds the durable pause | `Modules/Steering/ApprovalCoordinator.cs` (auto/light/firm; pending → approved → executed) |
| 8 | **Actuator / Effect Executor** | steering (device) | Idempotent executor invoking approved proposals through the kernel (filter still applies) | `Agent/GoalAgent.cs` (`ApplyApprovalAsync`) + `ApprovalCoordinator.MarkExecuted` |
| 9 | **Scheduler / Temporal** | steering (device) | Generic clock abstraction — real or simulated, NEVER hardcoded | `Modules/Steering/Clock.cs` (`IClock`, `SystemClock`, `SimulatedClock`) |
| 10 | **Monitor & Adapt** | steering (device) | Change watcher + deterministic materiality policy → scoped adaptation `proposal` | `Modules/Steering/MonitorAdapt.cs` (`MonitorAdapt`, `MaterialityPolicy`, `WorldChange`) |
| 11 | **Trace / Explain** | steering (device) | `Microsoft.Extensions.Logging` (structured, correlation-scoped) + streamed `agent_event` frames | `Modules/Steering/Trace.cs` |

Goal-side modules (#1, #2) are cloud-owned; the device sees their *output* in
the dispatch. Everything else is a real class in this repo, advertised to the
cloud/UI in the `capabilities` message (`kind: "steering"` entries come from
`CapabilityRegistry.SteeringModules`).

## Capability modules (SK plugins — the LLM's tools)

All in `Modules/Capabilities/`, registered in `GoalAgent.BuildKernel`,
advertised with `kind: "capability"` and per-function
`side_effecting`/`tier` metadata. Mock-world access goes through
`MockWorldStore` (relative dates; see `data/README.md`).

| Plugin (module name) | Domain | [KernelFunction]s (side-effecting → tier) |
|---|---|---|
| `InventoryPlugin` (Inventory) | meal | `ListItems`, `GetExpiringItems`, `CheckAvailability`, `ConsumeItem` → auto |
| `CalendarPlugin` (Calendar) | shared | `GetEvents`, `GetBusyEvenings`, `AddEvent` → light |
| `RecipePlugin` (Recipes) | meal | `FindRecipes`, `GetRecipe` |
| `ShoppingListPlugin` (ShoppingList) | shared | `GetList`, `Add` → light, `Remove` → light, `PlaceOrder` → **firm** (spends money) |
| `ReminderPlugin` (Reminders) | shared | `List`, `Create` → auto, `Delete` → auto |
| `ApplianceControlPlugin` (Appliance) | shared (SmartThings) | `ListAppliances`, `PreheatOven` → light, `RunProgram` → light, `Defrost` → auto *(signatures only; guest_dinner milestone)* |
| `FamilyProfilesPlugin` (FamilyProfiles) | shared | `GetProfiles`, `GetMember` *(signatures only)* |
| `BudgetPlugin` (Budget) | shared | `GetBudgetStatus`, `EstimateCost` *(signatures only; enforcement is SafetyFilter's)* |
| `NotifyPlugin` (Notify) | shared | `SendNotification` → auto, `Announce` → light *(signatures only)* |

**Generality in one line:** the `guest_dinner` domain adds a Guests/RSVP
plugin and *reuses* Calendar, Reminders, Appliance, ShoppingList, Recipes and
FamilyProfiles — same steering modules, same protocol, different toolbox
subset. Not every goal uses every module; the pipeline is composed from the
registry per goal.
