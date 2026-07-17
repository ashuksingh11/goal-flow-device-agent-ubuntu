# AGENTS.md — goal-flow-device-agent-ubuntu (coding-session guide)

Context for an AI/coding session in this repo. Read first.

## What this repo is

The **device agent** of GoalFlow — a two-tier goal-based agent POC for the Samsung
Tizen Family Hub. This tier is the **sole authority on local state** and the only
thing that touches actuators. It is a **.NET 8 console app on Microsoft Semantic
Kernel**: an SK kernel with capability plugins + auto function-calling planning, a
deterministic safety filter, a generic clock, and structured logging. It connects
outbound (WS client) to the cloud hub. **"LLM plans, code checks."**

Siblings under `~/ashu/git/`: `goal-flow-cloud-agent` (Python hub, owns the canonical
`CONTRACT.md`), `goal-flow-agent-chat-ui` (React UI), `goal-flow-device-agent-tizen`
(frozen port — see its AGENTS.md). This repo's C# `Contracts/*.cs` MIRROR the cloud's
`CONTRACT.md`.

**Tizen sync status: OUT OF SYNC since v3-M0** (2026-07-17). `Modules/` no longer
exists — it split into `Harness/` (generic core) + `Products/FamilyHub/` (product
pack) — so the old copy recipe names a directory that is gone and the Tizen ports
will not build against this tree. This is EXPECTED and accepted: both Tizen repos are
frozen pre-v2 by decision, and v3 re-syncs them ONCE at M9 rather than taxing every
milestone. Do not attempt a partial re-sync before then.

When M9 comes: this repo is the SOURCE OF TRUTH for the portable core, and the recipe
becomes a plain copy of `Agent/`, `Contracts/`, `Harness/`, `Products/`, `Transport/`
(five dirs, was four) + a `dotnet build`, then verify with
`diff -rq <dir> ../goal-flow-device-agent-tizen/<dir>` (must be empty).
NEVER copy the host files — each platform owns its own (`Program.cs` here;
`Program.cs`/`DeviceHost.cs`/`DeviceConfig.cs`/`DlogLogger.cs`/`AssemblyResolver.cs`/
`UiChannel.cs` there). A core change that needs host wiring (like `device_id`) must be
wired separately in each host.

## Stack & run

- .NET 8 + Microsoft Semantic Kernel + OpenRouter. **LLM-only planning** — there is
  NO `--planner` flag and no RulesPlanner/ScriptedPlanner (removed in the v2 rebuild).
- **Run via the project file, NOT `--no-build` from the repo root:**
  `dotnet run --project GoalFlow.Device.csproj -- <mode>`.
  GOTCHA: the repo has both a root `GoalFlow.Device.csproj` and
  `src/GoalFlow.Device/GoalFlow.Device.csproj` plus a `.sln`; `--no-build` from root
  can run a STALE `bin/` binary and silently mask your edits. Always let it build.
- Modes: `--goal "<text>"`, `--contract <path>`, `--connect [<ws url>]`
  (defaults to `$WS_URL` then `ws://localhost:8000/ws`; retries with backoff,
  survives cloud restarts), `--simulate-week`, `--simulate-guest` (headless
  adaptation sims — the fastest way to verify planning/adaptation).
- Env: `OPENROUTER_API_KEY` etc. (same OpenRouter vars as the cloud).
- **Multi-session identity:** the cloud is now multi-session (many UIs + many
  device agents, paired by `device_id`; a "home" = 1 device + N UIs). This agent
  sends `device_id`/`device_name` in `hello`. `device_id` = `--device-id` /
  `$DEVICE_ID`, else a persistent self-generated UUID stored in `<data>/device_id`
  (plain File I/O, generated once, reused across restarts — same scheme will run
  on the Tizen Hub). `device_name` = `--device-name` / `$DEVICE_NAME`, else
  `user@machine (<short-id>)` (must be recognisable AND unique — the UI shows a picker when several agents
  are connected). See `ProgramHelpers.ResolveDeviceId` / `ResolveDeviceName` in
  `Program.cs`.
- **Running two agents side by side** (multi-session test): each needs its own
  `--data` dir so their mock worlds don't clobber each other — `EnsureDataDir`
  auto-seeds a `--data` dir with no `*.json` from `./data` on first use (never
  overwrites a populated world), so this just works instead of dying on a missing
  `calendar.json`:
  `dotnet run --project GoalFlow.Device.csproj -- --connect ws://localhost:8000/ws --data ./data-a`
  (device_id auto-generates; add `--device-id hub-a` only for a deterministic id).
  `.gitignore` excludes `data-*/`.

## Architecture / key files

- `Agent/GoalAgent.cs` — the SK kernel host. Grounding → two-phase compose (a
  tools/grounding pass, then a no-tools JSON-plan pass) → checking → `plan_ready`.
  `ApplyApprovalAsync` actuates approved proposals. `HandleControlAsync` /
  `HandleControlCoreAsync` handle clock controls AND **`trigger_event`** (handled
  first): fire one presenter event → `ProposeDailyAdaptationAsync` (ONE scoped-LLM
  patch, no grounding tool-loop) → status with `updated_plan`/`changed_ids`/
  `impact_delta`/`event_id`. Plan items get a **1-based `Day`**; meal plans are
  capped/pinned to EXACTLY 7 dinners (Day 1..7). Adaptation targets the item by its
  event's `Day` index (not by date). Transient-provider-error retry loop wraps LLM calls.
- `Products/FamilyHub/**` — THE PRODUCT PACK: everything fridge-specific.
  - `FamilyHubProduct.cs` — the manifest. **The single place the plugin catalog is
    declared** (`services.AddFamilyHub(dataDir)`); it used to be hand-listed in four
    places that could disagree. Adding a plugin = one line here + the plugin file.
    **Descriptor ORDER is significant** — it is the order the model sees its tools in.
  - `Plugins/*Plugin.cs` — 10 SK plugins (KernelFunctions the LLM calls). 7 implemented
    (Inventory, Calendar, ShoppingList, Recipes, Reminders, Guests, ApplianceControl);
    3 genuine stubs that throw `NotImplementedException`, each marked
    `[Unavailable(...)]`: `FamilyProfilesPlugin`, `BudgetPlugin`, `NotifyPlugin`. That
    attribute is **load-bearing, not decoration** — stubs' reads look real to
    reflection, so without it the planner gets 17 tools instead of 13, four of which
    throw. Implementing a stub = write the bodies, delete the attribute, same diff.
  - `Adapter/MockFamilyHubAdapter.cs` — the mock world (`data/*.json`) behind
    `IProductApiAdapter`. The world stays MOCKED; "generic" is about the harness.
- `Harness/**` — THE GENERIC CORE. **Contains zero product types and imports the
  product namespace zero times** (enforced: `verify/m0/check.sh` gate 3 pins the
  count of product string literals so it can only shrink). The five v3 components plus
  the v2 harness modules reconciled onto them:
  - `CapabilityManager/` — discovery over `kernel.Plugins` + the pack's descriptors;
    builds the `capabilities` message; **derives the planner's tool set** (there is no
    hand-written whitelist, and no name→Type switch: reflection reads the live
    registered instance).
  - `SafetyPolicyEngine/` — the safety gate, SEPARATE from the planner.
    `SafetyFilter` is still the `IFunctionInvocationFilter` seam (every tool call
    passes through it), but since v3-M1: the policy is **per goal**
    (`BeginGoal`/`EnterGoal` + an `AsyncLocal`; a singleton field made the gate
    unsound with two goals — see the M1 commits), the checks are **declarative**
    (`Products/FamilyHub/config/policy.json` binds harness rule KINDS to this
    product's calls), grades are **A0/A1/A2/AX** with a **ratchet** (config may
    only tighten; weakening is fatal at startup), and term matching is
    token/stem-based (`allergens:["peanuts"]` blocks "peanut butter" — v2's
    substring check did not — while still allowing coconut under a nut allergy).
    `constraints.hard` remains its only input.
  - `TaskManager/` — THE GOAL LEDGER (v3-M2). `TaskManager` + `GoalRecord` +
    `TaskRecord` + `TaskState`: the task DAG, a validated lifecycle (illegal moves
    are refused, not applied), retries, and **derived** progress — which is what
    makes Agent Board's %/next-step/pending facts rather than clock heuristics.
    `TaskDag` sanitizes the planner's decomposition (drops unknown/self deps, caps
    at 8, breaks cycles) because an LLM suggestion must never corrupt the ledger.
    `MonitorAdapt` is now pure orchestration: it asks the pack's `IDomainObserver`s
    what changed, dedups, and proposes. It knows no product.
  - `ProductApiAdapter/IProductApiAdapter.cs` — the product seam: everything the
    harness/plugins may touch of the world. A real Tizen/SmartThings port implements
    THIS and changes nothing else.
  - `Approval/` `Grounding/` `Clock/` (`IClock`/`SystemClock`/`SimulatedClock` — NEVER
    call wall-clock, read the clock) `Trace/`.
  - `PrecheckEngine/` — IS THE WORLD READY? (v3-M3). The third gate, and it asks a
    different question from the other two: safety says "never", approval says
    "waiting on a person", a precheck says "not YET" — something is unplugged and
    it will resume. Two gates at phase boundaries (NOT in the SK filter, which
    stays safety-only): before planning (fail → the goal waits, with a remediation
    a person can act on, before a token is spent) and before actuating each
    approved effect (fail → `deferred_precheck`; the approval still stands, so it
    executes when the world recovers). Probes are product knowledge and live in the
    pack (`Probes/`, bound by `config/prechecks.json`, reading `data/device_state.json`).
- `Contracts/*.cs` — C# mirror of CONTRACT v2. `PlanReady.cs` has `PlanItem.Day`,
  `DemoEvent {Id,Day,Label,Title,Kind,Order}`, `DemoEvents` catalog. `Control.cs` has
  `EventId` + `TriggerEvent` const. `Status.cs` has `EventId`/`UpdatedPlan`/
  `ChangedIds`/`ImpactDelta`. `Proposal.cs` has `EventId`. `Hello.cs` has
  `DeviceId`/`DeviceName` (`HelloAck` has `DeviceId` too) — the pairing identity for
  multi-session cloud routing. JSON serializes snake_case.
- `Transport/WsClient.cs` — one outbound BCL `ClientWebSocket`; ctor now takes
  `deviceId`/`deviceName` and sends them in the `hello` frame.
- `Program.cs` / `ProgramHelpers` — CLI entry + DI root. `ResolveDeviceId` /
  `ResolveDeviceName` resolve this agent's pairing identity (see Stack & run above);
  `EnsureDataDir` seeds a fresh `--data` dir from `./data`.
- `data/` — seeded world state. `data/daily_events.json` is the **presenter-fired
  event catalog** (6 events: day1 restock, day2 shortage, day3 football, day4 guest,
  day5 oven, day6 "Lighter Sunday") that drives the event-driven meal demo; each has
  `id/label/title/order/day/kind/summary/context/steer`.

## Contract touchpoints

Sends: `hello` (now with `device_id`/`device_name` — see Stack & run), `capabilities`,
`agent_event` (thinking/tool-call stream), `plan_ready` (plan + tiered proposals +
`demo_events` + safety), `proposal` (adaptations, tier `adapt`), `status` (monitoring
+ `updated_plan` on adaptation). Receives: `hello_ack` (echoes the bound `device_id`),
`dispatch` (Task Contract), `approval`, `control` (`advance_day`/`reset`/`set_date`/
`trigger_event`).

## Current state

Both domains fully built and verified (headless sims + live). Event-driven meal demo,
scoped-LLM daily/event adaptation, guest prep-timeline + appliance gating all work.
Stubs: FamilyProfiles/Budget/Notify plugins.

## Conventions & gotchas

- **Commit identity:** author as `ashuksingh11`
  (`31301999+ashuksingh11@users.noreply.github.com`). **Push only when asked.**
- **Workflow:** plan=Opus · design=Fable · coding=Codex CLI · browsing=Sonnet.
- **Do NOT `git checkout -- data/` blindly:** `data/daily_events.json` is now a
  STRUCTURAL file (labels + the 6th event), not just runtime-mutated seed — reverting
  it drops the 6th event. Only reset appliances/shopping_list/inventory/calendar/
  guests/recipes/reminders.json.
- The device's domain string must match the cloud's canonicalized `meal_plan` /
  `guest_dinner` exactly, or monitoring/per-day planning won't route.
