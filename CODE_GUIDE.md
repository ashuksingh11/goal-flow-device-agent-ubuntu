# Code Guide — goal-flow-device-agent-ubuntu (v3)

The on-device agent is a .NET 8 process where **the device IS a Semantic
Kernel agent**: device capabilities are SK **plugins** the LLM calls via auto
function-calling, and every harness module is either one of those plugins
("capability") or deterministic code that shapes/guards the run ("steering").
Planning is **LLM-only** — there is no rules or scripted planner anywhere in
the agent. Built Linux-first; the Tizen port swaps plugin internals, never the agent.

Companion docs: `docs/ARCHITECTURE.md` (kernel/filter/stream design),
`docs/HARNESSES.md` (the five harness components → real primitives),
`../goal-flow-agents/docs/V2_DESIGN_PROPOSAL.md` (the framing).

## File map

```
GoalFlow.Device.sln / GoalFlow.Device.csproj   # sln builds src/; root csproj lets `dotnet run` work from the repo root
data/                                # mock world — ALL dates are day offsets (data/README.md)
  inventory.json calendar.json recipes.json shopping_list.json reminders.json
  guests.json appliances.json family.json budget.json notifications.json
  security.json party.json vacation.json grocery.json energy.json   # grocery/energy are the v3.4 additions
  device_state.json                  # runtime flags the pre-check probes read (appliance_online, samsung_account, …)
  daily_events.json                  # presenter-fired event catalog for control: trigger_event
  sample-contract.json               # meal_plan dispatch (${today+N} tokens)
  sample-contract-guest.json         # guest_dinner dispatch
  sample-approval.json
src/GoalFlow.Device/
  Program.cs                         # CLI + DI composition root                ← start here
  Agent/GoalAgent.cs                 # SK kernel host: BuildKernel / RunAsync / ApplyApprovalAsync / HandleControlAsync
  Contracts/                         # C# mirror of CONTRACT v3 (Dispatch, PlanReady, Proposal, Approval, Status, Control, AgentEvent, Capabilities, …)
  Harness/                           # THE GENERIC CORE — no product types, no LLM inside
    CapabilityManager/               # [1] the toolbox: discovery, advertisement, the planner's tool set
      CapabilityManager.cs           #     discovery over kernel.Plugins + the pack's descriptors
      CapabilityDescriptor.cs        #     name + live instance + availability (ORDER is significant)
      SideEffectAttribute.cs         #     a function's intrinsic risk, declared where it lives
      UnavailableAttribute.cs        #     "declared but not built" → withheld from the planner
    SafetyPolicyEngine/
      SafetyFilter.cs                # [2] SK IFunctionInvocationFilter — the safety gate; policy is PER GOAL
      SafetyPolicy.cs                #     loads policy.json; the grade ratchet (config may only tighten)
      SafetyRule.cs                  #     the rule KINDS (blocked_terms, numeric_cap, time_window_block, …)
      TermMatcher.cs                 #     token/stem matching: "peanuts" blocks "peanut butter", not "coconut"
      AutomationGrade.cs             #     A0/A1/A2/AX + the v2 tier mapping
    TaskManager/                     # [4] THE GOAL LEDGER — what makes Agent Board honest
      TaskManager.cs                 #     goals, the DAG, validated transitions, derived progress
      TaskRecord.cs                  #     TaskState (12) + TaskRecord (deps, retries, failure)
      TaskDag.cs                     #     sanitizes the LLM's decomposition (cycles, unknown deps, cap)
      IDomainObserver.cs             #     the seam: the PACK watches the world, decides materiality
      MonitorAdapt.cs                #     orchestration only: ask observers, dedup, propose
    ProductApiAdapter/
      IProductApiAdapter.cs          # [5] THE PRODUCT SEAM — all the harness may touch of the world
    Approval/ApprovalCoordinator.cs  # tiered proposal ledger (pending → approved → executed)
    Grounding/Grounding.cs           # planner context assembler (clock, constraints verbatim, digest)
    Clock/Clock.cs                   # IClock + SystemClock + SimulatedClock (generic clock)
    Trace/Trace.cs                   # agent_event streaming + structured logging (one sink, two audiences)
    PrecheckEngine/                  # [3] IS THE WORLD READY? — "not yet", vs safety's "never"
      IPrecheckProbe.cs              #     a probe + its verdict; the remediation is the point
      PrecheckEngine.cs              #     two gates (before planning, before actuating) + bindings
  Products/FamilyHub/                # THE PRODUCT PACK — everything fridge-specific
    FamilyHubProduct.cs              # the manifest: THE single declaration of the plugin catalog
    Observers/                       # the six IDomainObservers (one per domain): MealPlan (daily feed),
                                     #   GuestDinner (RSVPs), VacationPrep, BirthdayParty, GroceryCost, EnergySaving
    Probes/                          # what this product can check: device flags, appliance_online:<id>
    config/prechecks.json            # which of THIS product's calls need which checks
    Adapter/MockFamilyHubAdapter.cs  # the mock world; resolves day offsets against IClock at read time
    config/policy.json               # THIS product's safety rules: which kinds bind to which calls
    Plugins/                         # 11 SK plugins — the LLM's tools (ALL implemented; no stubs)
      InventoryPlugin.cs CalendarPlugin.cs RecipePlugin.cs ShoppingListPlugin.cs
      ReminderPlugin.cs GuestsPlugin.cs ApplianceControlPlugin.cs
      FamilyProfilesPlugin.cs BudgetPlugin.cs NotifyPlugin.cs SecurityPlugin.cs
      # 18 grounded READ tools + 14 [SideEffect] proposable functions across the 11
      # (FamilyProfiles/Budget/Notify were [Unavailable] stubs in early v3; M7 filled them in)
  Transport/WsClient.cs              # one outbound BCL ClientWebSocket to the cloud hub
verify/m0/ … verify/m5/            # the gates — run the LATEST milestone's check.sh before every commit
```

**The split is the point.** `Harness/` is domain- and product-agnostic; `Products/`
holds every fridge string. Namespaces are FLAT (`GoalFlow.Device.Harness`,
`GoalFlow.Device.Products.FamilyHub`) — folders carry the structure, because
per-folder namespaces would collide with `Trace.Trace` / `Grounding.Grounding` /
`CapabilityManager.CapabilityManager`.

## Entry point (`Program.cs`)

Parses CLI options, loads `.env`, then builds the composition root with
`Microsoft.Extensions.DependencyInjection`: the `IClock` (real `SystemClock`
by default; `SimulatedClock` when `--date` or a simulation mode is used), then
**`services.AddFamilyHub(dataDir)`** — the one line that knows what product this
is, registering the adapter, the eleven plugins, the six domain observers and
the `CapabilityManager` over them — and finally the harness components (`SafetyFilter`, `ApprovalCoordinator`,
`Grounding`, `MonitorAdapt`).
`GoalAgent.BuildKernel` then assembles the SK kernel from that provider, looping
the pack's descriptors — it names no plugin type.
`ProgramHelpers.EnsureDataDir` runs first and auto-seeds a `--data` dir with
no `*.json` from `./data` (so a second instance's `--data ./data-b` doesn't
die on a missing `calendar.json`); in `--connect` mode,
`ResolveDeviceId`/`ResolveDeviceName` then resolve this run's pairing identity
(persisted UUID in `<data>/device_id`, or `--device-id`/`--device-name` /
`$DEVICE_ID`/`$DEVICE_NAME` overrides) before the `WsClient` is constructed.

| Command | What it does |
|---|---|
| `--goal "…" [--domain meal_plan]` | Synthesizes a local `Dispatch` around the goal text and plans it. |
| `--contract <file> [--approval <file>]` | Runs a dispatch file → `plan_ready` on stdout; optionally applies an approval, then **replays it** to prove idempotency. `${today+N}` tokens resolve against the clock at load. |
| `--connect <ws url>` | Live cloud session: `hello` (with `device_id`/`device_name`) → `hello_ack` → `capabilities`, then routes inbound `dispatch`/`approval`/`control` to `GoalAgent` (each frame handled on a background task so planning never starves WS keepalives). |
| `--simulate-week` / `--simulate-guest` | Plan the meal / guest contract, then issue `advance_day` controls (5 / 2 ticks), printing `status` frames and — on the material day — the adaptation `proposal` plus an approve+replay round-trip. Runs on a temp copy of `data/`. |
| `--date <ISO>` / `--data <dir>` / `--device-id <id>` / `--device-name <name>` / `--verbose` | Simulated clock start; mock-world dir; pairing identity overrides; debug logging. |

## The plan flow (`Agent/GoalAgent.cs → RunAsync`)

One dispatch becomes one `plan_ready`, with `agent_event` frames streamed the
whole way (via `Trace`; over the WebSocket when connected, stderr otherwise).

**The planner has TWO ALTITUDES** (v3-M2), and the split is deliberate:

0. **decompose** — *what are the pieces of this goal?* A JSON-mode call with NO
   tools over the advertised capabilities, asking only for structure (titles,
   dependencies). It must not state world facts — it cannot see the world, and a
   toolless model asked about the fridge would invent. `TaskDag.Sanitize` then
   repairs the result (an LLM proposes, code validates — the same division as the
   safety gate), and any failure **falls soft** to one task so a decomposition
   problem never costs the user their goal.

1. **`phase: grounding`** — `SafetyFilter.BeginGoal(goal_id, constraints.hard)` arms
   the gate; `Grounding.AssembleAsync` resolves *today* and the contract's
   time window against the generic clock and renders the system prompt
   (hard constraints **verbatim** — the model sees the same truth the filter
   enforces). Then a **streaming** chat call runs with
   `FunctionChoiceBehavior.Auto` scoped to the **read-only** function subset
   DERIVED by `CapabilityManager.GetGroundingFunctions` (every non-side-effecting
   function of every available plugin, in the pack's order — no hand-written
   whitelist; `verify/m0` gate 2 pins the result). The LLM grounds itself by *calling*
   those `[KernelFunction]`s; every call passes through the `SafetyFilter`,
   which also emits the `tool_call`/`tool_result` events. Model text streams
   as `thinking` events.
2. **`phase: planning`** — the **two-phase compose** (`ComposeModelPlanAsync`):
   a second, **no-tools** chat call with `ResponseFormat = "json_object"` asks
   for the final plan as one JSON object (`plan[]`, `proposals[]`, `impact[]`,
   `explanation`). The compose prompt (`BuildPlanningInstruction`) injects
   `PlanShapeRule(dispatch.Domain)` — **only the active domain's** plan shape,
   not a hardcoded "7 dinners" (that bleed, a vacation planned as a week of
   meals, was the v3.5 bug this fixed; only `meal_plan` asks for exactly seven).
   Side effects are deliberately *not* exposed as tools during planning — the
   model must *propose* mutations as `{module, function, args, tier}` entries
   naming real side-effecting functions, drawn from the **one proposal catalog**
   of all 14 `[SideEffect]` functions the prompt lists for every domain.
   Robustness: up to 2 compose attempts with a parser-error retry prompt; if the
   provider rejects/ignores `response_format`, it falls back to a strict-JSON
   prompt; code-fence stripping + brace-matched extraction before
   `JsonSerializer`. Then `AssignPlanDays(plan, domain)` stamps each item's
   1-based `Day`: `meal_plan` keeps the ordinal Day 1..7, every OTHER domain
   derives Day from the item's own `when` date (`ParseWhenDate`, relative to the
   earliest item) so same-day work shares a day. Day is not cosmetic — the
   device completes a goal at `Plan.Max(p => p.Day)` and the cloud sizes its
   progress window from the same span.
3. **`phase: checking`** — each proposal is normalized (tier overridden from
   the function's `[SideEffect]` attribute via
   `CapabilityManager.GetSideEffectTier`; `requires_approval = true`; id
   assigned if missing) and registered in the `ApprovalCoordinator` ledger.
   Plan items stream as `plan_progress` events. The `SafetyFilter`'s verdict
   (`gate: passed|blocked` + recorded violations) becomes `payload.safety`.
4. **`phase: awaiting_approval`** — the `plan_ready` frame is returned, and
   the goal is remembered as an `ActiveGoalContext` (dispatch + plan + a
   world snapshot from `MonitorAdapt.CaptureSnapshotAsync`) for the sustain
   loop.

**Actuation** (`ApplyApprovalAsync`): an `approval` frame's decisions flip
ledger entries; each cleared proposal's frozen `{module}.{function}(args)` is
invoked **through the kernel** (`_kernel.InvokeAsync` — the SafetyFilter still
applies), then `MarkExecuted` makes replays no-ops. Returns a `status` frame
listing executed effects.

## The three gates — "LLM plans, code checks"

The planner and the safety check are different *mechanisms*, not just
different classes:

- The **LLM** decides what to do — which tools to call, what to cook, what to
  propose. It is fallible by assumption.
- The **`SafetyFilter`** (`Harness/SafetyPolicyEngine/SafetyFilter.cs`) is an SK
  `IFunctionInvocationFilter` sitting in the kernel's invocation pipeline, so
  *every* function call — grounding reads and approved actuations alike —
  passes through `OnFunctionInvocationAsync` first. On violation it does not
  call `next` — the plugin never runs, `context.Result` becomes a structured
  refusal (so the model sees why and re-plans), and the violation lands in
  the `plan_ready` safety verdict.
  - **The policy is PER GOAL** (`BeginGoal` on the plan path, `EnterGoal` on the
    approval/control paths; the ambient goal rides an `AsyncLocal`). It was one
    field on a singleton until v3-M1, which made the gate unsound as soon as two
    goals overlapped — and Program has always dispatched frames concurrently.
    A call outside any scope enforces nothing and says so loudly
    (`safety_unscoped`); it no longer inherits a stranger's policy.
  - **The checks are declarative** (`Products/FamilyHub/config/policy.json`). The
    harness implements rule KINDS — `blocked_terms`, `numeric_cap`,
    `time_window_block`, `result_screen` — and the product pack says which of its
    calls each applies to, plus its ingredient vocabulary (dairy → milk/paneer/…).
    Before v3 this was a chain of hardcoded `module == "ShoppingList"` comparisons
    inside the filter.
  - **Term matching is token/stem-based** (`TermMatcher.cs`), so `allergens:
    ["peanuts"]` blocks "peanut butter" — v2's substring check did not — while
    still allowing coconut, butternut squash and nutmeg under a "nuts" allergy.
    Over-blocking is a real failure mode, not a safe default: an agent that
    vetoes coconut gets switched off.
  - **`constraints.hard` remains its ONLY input.** Soft preferences never gate.
- The **pre-check gate** (`Harness/PrecheckEngine/`, v3-M3) asks the question the
  other two don't: is this POSSIBLE right now? A plan that preheats an unplugged
  oven passes safety (nothing forbids it) and passes approval (the user said yes)
  and then fails in the kitchen. It runs at phase boundaries — before planning,
  and again before actuating each approved effect, because the world moves while
  the user is deciding. A failure DEFERS (`deferred_precheck`), it does not fail:
  the approval still stands and executes when the oven comes back.
- The **approval gate** is the user: `[SideEffect]`-tagged functions surface
  as tiered proposals (`auto` = cheap/reversible, `light` = batched into the
  plan approval, `firm` = spends money / irreversible — never executes before
  an explicit decision). `ApprovalCoordinator` owns the ledger and the
  idempotency.

Verify it: put an allergen a seeded recipe contains into `constraints.hard`
and watch the filter block with `safety.gate: "blocked"` + the recorded
`violations`.

## The sustain / adaptation loop (`Harness/TaskManager/MonitorAdapt.cs`)

After a plan is out, `control: advance_day` (or `set_date`) frames drive
`GoalAgent.HandleControlAsync`:

1. The `SimulatedClock` steps; `control: reset` restores the pristine mock
   seeds instead.
2. `MonitorAdapt.ObserveAsync` asks each registered `IDomainObserver` (the
   product pack, `Products/FamilyHub/Observers/`) to diff its slice of the
   active goal's world snapshot against the new *today* — e.g.
   **MealPlanObserver** looks for a calendar event (the football night)
   overlapping a planned dinner's prep window; **GuestDinnerObserver** looks
   for guest `pending_updates` activating today (an RSVP allergy added, a late
   arrival); the other four domains watch their own documents.
3. Each `WorldChange` arrives already classified `material` **by the observer** —
   deterministic code, not the LLM (materiality is product knowledge: only the
   domain knows a guest's nut allergy matters and a restocked staple does not).
   Quiet days return `material: false` status frames; each material change
   fires once (deduped by the harness via `EmittedMaterialChanges`).
4. On a material change, `ProposeAdaptationAsync` registers a scoped,
   `adapt`-tier proposal in the same ledger (e.g. a night-before prep
   reminder, or nut-free backup shopping items) — it rides the same approval
   → actuation path as plan proposals.

`--simulate-week` and `--simulate-guest` are exactly this loop, headless.

### The event-driven meal demo (`control: trigger_event`)

A presenter-fired sibling of the clock-driven loop above, scoped to
`meal_plan`. `PlanReadyPayload.DemoEvents` (built by
`MonitorAdapt.GetDemoEventsCatalog` from `data/daily_events.json`) ships a
catalog of 6 events as UI chips — each with a `day` (targets a `PlanItem.Day`),
`kind`, `summary`, `context`, and `steer`. Firing a chip sends
`control: trigger_event { event_id }`, handled by
`GoalAgent.HandleControlCoreAsync` *before* any clock stepping (the clock is
frozen for this path — `set_date`/`advance_day` handling only runs after the
`trigger_event` branch returns):

1. Looks up the event by id in the active goal's world snapshot; missing/
   unknown/already-applied (`EmittedMaterialChanges`) ids short-circuit with a
   `material: false` status.
2. `MonitorAdapt.BuildDailyEventChange` turns it into a `WorldChange`;
   `ProposeDailyAdaptationAsync` runs the same **scoped** LLM re-plan as the
   sustain loop, but seeded with just that event's `context` + `steer` against
   the plan item for its `day` — not the whole week.
3. Returns a `status` (material change note) plus an adaptation `proposal`
   (a minimal `PlanPatch`) that rides the normal approval → actuation path.

## Capability vs steering — the harnesses as real primitives

The "11 harness modules" of the v2 design are not conventions; each is a real
type here or a real SK feature (full table in `docs/HARNESSES.md`):

- **Capability modules** = SK plugins in `Products/FamilyHub/Plugins/`, registered
  in `GoalAgent.BuildKernel` under their advertised names (`Inventory`,
  `Calendar`, `Recipes`, `ShoppingList`, `Reminders`, `Guests`, `Appliance`,
  `FamilyProfiles`, `Budget`, `Notify`, `Security`). Registration IS the action space.
  Side-effecting methods carry `[SideEffect(tier)]`; all world access goes
  through `MockFamilyHubAdapter` (writes persist to `data/*.json`).
- **Steering modules** = the deterministic classes in `Harness/`:
  Planner host (`GoalAgent` + SK auto function-calling), Safety
  (`IFunctionInvocationFilter`), Approval (ledger), Grounding, Scheduler
  (`IClock`), MonitorAdapt (delegating materiality to the product's
  `IDomainObserver`s), Trace, and
  `CapabilityManager` — which builds the `capabilities` advertisement by
  *discovery* (walking `kernel.Plugins` metadata + `[SideEffect]` reflection),
  sent right after `hello_ack` in `--connect` mode.

**Domain generality in one line:** `guest_dinner` adds the `Guests` plugin
and reuses Calendar, Recipes, ShoppingList, Reminders, Appliance — same
kernel host, same steering modules, same protocol, different toolbox subset.

## The generic clock (`Harness/Clock/Clock.cs`)

Nothing reads the wall clock or hardcodes a date. `IClock` (`Now`, `Today`)
has two implementations: `SystemClock` (real date; the default) and
`SimulatedClock` (starts at real today or `--date`; driven by `set_date` /
`advance_day` controls). Mock data stores **day offsets**
(`expires_in_days`, `day_offset`, `due_in_days`, `${today+N}` contract
tokens) that `MockFamilyHubAdapter` resolves against `IClock.Today` at read time —
the seed world is always "this week" no matter when or under what clock it
runs. Never call `DateTime.Now`; inject `IClock`.

## Tizen-lean dependency rule (hard)

This process ports to Tizen.NET on the Family Hub. The ONLY NuGet packages
allowed (enforced and documented in both csproj files):
`Microsoft.SemanticKernel`, `Microsoft.Extensions.Logging` (+ `.Console`),
`Microsoft.Extensions.DependencyInjection`. Everything else is BCL —
`System.Text.Json`, `System.Net.WebSockets.ClientWebSocket`. Adding any other
package breaks the port.

## Extending it

**Add a capability plugin** (new device function):

1. Create `Products/FamilyHub/Plugins/FooPlugin.cs`: public methods tagged
   `[KernelFunction]` + `[Description]` (descriptions are the LLM's tool
   docs); mutating methods get `[SideEffect(ApprovalTiers.…)]`; read/write
   the world via `IProductApiAdapter`. If you are landing it as a stub that
   throws, mark the class `[Unavailable("…")]` — that keeps it advertised as an
   extension point while withholding it from the planner, so the model is never
   handed a tool that throws. Delete the attribute in the same diff that writes
   the bodies.
2. Register it in `Products/FamilyHub/FamilyHubProduct.cs` — a DI singleton and
   a `CapabilityDescriptor.From("Foo", …)` entry. **That's the whole
   registration.** Mind where you put the descriptor: the list order is the
   order the model sees its tools in.

Nothing else. The `capabilities` advertisement, the planner's grounding tool set
(read functions, automatically), valid proposal targets, and the UI's module view
all follow from that one registration.

*(Until v3-M0 this said the same thing while listing four places to edit —
`Program.cs`, `BuildKernel`, a `PluginType` switch, and a 13-entry read-only
whitelist — any of which you could forget. Those are gone; `verify/m0/check.sh`
gate 2 pins the derivation.)*

**Add a domain:** write a dispatch with the new `domain` + `scope`/`context`,
add whatever plugins the domain needs (above), add its `PlanShapeRule` arm, and —
if it should adapt over time — register an `IDomainObserver` in
`FamilyHubProduct` that captures its slice of the world and classifies its
changes' materiality (that registration IS the domain the cloud routes on; the
six shipped observers live in `Products/FamilyHub/Observers/`). The kernel host,
safety filter, approval ledger, clock, trace, and protocol need no changes.

**Real actuators (Tizen):** keep every plugin's `[KernelFunction]` signature
and replace its `MockFamilyHubAdapter` internals with real Tizen/SmartThings calls.
The agent, filters, and contracts don't change.

## Run & verify

# Full-stack demo commands live in goal-flow-agents/docs/FINAL_DEMO.md. Driving the device alone:
```bash
dotnet build GoalFlow.Device.csproj
dotnet run --project GoalFlow.Device.csproj -- --contract data/sample-contract.json          # plan_ready: plan + tiered proposals + safety verdict
dotnet run --project GoalFlow.Device.csproj -- --contract data/sample-contract.json --approval data/sample-approval.json   # execute + idempotent replay
dotnet run --project GoalFlow.Device.csproj -- --simulate-week                               # meal sustain: quiet days + the material day
dotnet run --project GoalFlow.Device.csproj -- --simulate-guest                              # guest sustain: RSVP/late-arrival adaptation
dotnet run --project GoalFlow.Device.csproj -- --connect                                     # attach to a cloud hub (defaults to ws://localhost:8000/ws)
```

Requires `OPENROUTER_API_KEY` (see README — planning is LLM-only). Frames on
stdout; logs and offline `agent_event`s on stderr.
