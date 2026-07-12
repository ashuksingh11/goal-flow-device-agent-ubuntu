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
- `Modules/Capabilities/*Plugin.cs` — 10 SK plugins (KernelFunctions the LLM calls).
  7 implemented (Inventory, Calendar, ShoppingList, Recipes, Reminders, Guests,
  ApplianceControl); 3 genuine stubs that throw `NotImplementedException("v2-M0
  skeleton")`: `FamilyProfilesPlugin`, `BudgetPlugin`, `NotifyPlugin`. (Accurately
  catalogued in `docs/HARNESSES.md`.)
- `Modules/Steering/*.cs` — `SafetyFilter` (an `IFunctionInvocationFilter`, deterministic,
  enforces `constraints.hard` only — the safety gate, SEPARATE from the planner);
  `ApprovalCoordinator`; `Grounding` (world-state assembler); `Clock` (`IClock` /
  `SystemClock` / `SimulatedClock` — NEVER call wall-clock, read the clock);
  `MonitorAdapt` + `MaterialityPolicy` (sustain tick → monitoring status, material
  change → proactive adaptation); `Trace`; `CapabilityRegistry`.
- `Contracts/*.cs` — C# mirror of CONTRACT v2. `PlanReady.cs` has `PlanItem.Day`,
  `DemoEvent {Id,Day,Label,Title,Kind,Order}`, `DemoEvents` catalog. `Control.cs` has
  `EventId` + `TriggerEvent` const. `Status.cs` has `EventId`/`UpdatedPlan`/
  `ChangedIds`/`ImpactDelta`. `Proposal.cs` has `EventId`. JSON serializes snake_case.
- `data/` — seeded world state. `data/daily_events.json` is the **presenter-fired
  event catalog** (6 events: day1 restock, day2 shortage, day3 football, day4 guest,
  day5 oven, day6 "Lighter Sunday") that drives the event-driven meal demo; each has
  `id/label/title/order/day/kind/summary/context/steer`.

## Contract touchpoints

Receives: `dispatch` (Task Contract), `approval`, `control` (`advance_day`/`reset`/
`set_date`/`trigger_event`). Sends: `capabilities`, `agent_event` (thinking/tool-call
stream), `plan_ready` (plan + tiered proposals + `demo_events` + safety), `proposal`
(adaptations, tier `adapt`), `status` (monitoring + `updated_plan` on adaptation).

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
