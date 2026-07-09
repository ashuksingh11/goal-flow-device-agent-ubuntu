# Harness Catalog

Every harness is one file in `src/GoalFlow.Device/Harnesses/`: a fully
specified interface + a skeleton class (bodies are `NotImplementedException`
in the design phase).

Build-effort tiers:
- **full** — real logic in the implementation phase (the core IP)
- **simple** — real but intentionally small
- **stub** — honest pass-through, named so the seam exists
- **adapter** — real interface + mock JSON data behind it

| Harness | Interface | Phase | Responsibility | Effort | M1 vs later |
| --- | --- | --- | --- | --- | --- |
| TaskManager | `ITaskManager` | orchestrate | Create task per contract, own goal_id mapping, drive status lifecycle (created→…→done) | full | M1 (create + planning/awaiting_approval transitions); full lifecycle later |
| PreCheck | `IPreCheck` | sense | Validate access/permissions before touching local APIs | stub (pass-through, logs "access validated") | M1 |
| CapabilityManager | `ICapabilityManager` | sense | Static registry of available local + cloud APIs | stub (hardcoded list) | M1 |
| Inventory adapter | `IInventoryApi` | sense | Fridge/pantry snapshot | adapter (`data/inventory.json`) | M1 |
| Calendar adapter | `ICalendarApi` | sense | Family events in a date range | adapter (`data/calendar.json`) | M1 |
| Recipe adapter | `IRecipeApi` | sense | Recipe catalog for the planner | adapter (`data/recipes.json`) | M1 |
| Shopping-list adapter | `IShoppingListApi` | act (actuator) | Read list; append approved items (idempotent) | adapter (`data/shopping_list.json`) | interface M1; writes with EffectExecutor |
| Reminder adapter | `IReminderApi` | act (actuator) | Read/create reminders (fire via Scheduler) | adapter (`data/reminders.json`) | interface M1; writes with EffectExecutor |
| Grounding | `IGrounding` | sense | Normalize all adapter data into ONE coherent `WorldState` (+ derived `ExpiringSoon`) | full | M1 |
| Planner | `IPlanner` (`RulesPlanner` default, `LlmPlanner` SK+OpenRouter, `ScriptedPlanner` canned) | decide | Contract + world + constraints → `CandidatePlan`; swappable via DI/config | full | M1 may be scripted/canned; rules next; LLM later |
| SafetyGate | `ISafetyGate` | gate (#1) | Deterministic CODE check of plan vs `constraints.hard`; blocks + returns `hard_violations`. Never the planner's job | full | M1 |
| ApprovalBroker | `IApprovalBroker` | gate (#2) | Freeze side-effects as proposals; track pending/approved/rejected/expired; correlate decisions by `correlation_id` | full | M1 (submit + status "awaiting_approval"); decision handling with transport |
| EffectExecutor | `IEffectExecutor` | act | Execute approved effects idempotently (dedupe on correlation_id); ledger of what was done | full | later (needs approvals flowing) |
| Scheduler | `IScheduler` | sustain | Fire time-based actions off the virtual clock (`IClock.WaitUntilAsync`) | simple | M4 |
| ChangeWatcher | `IChangeWatcher` | sustain | Detect world changes, apply materiality policy ("new calendar event overlapping a prep window → material"), re-invoke loop only if material | full | M4 |
| Trace | `ITrace` | cross-cutting | Structured record of every decision / tool call / gate outcome; doubles as demo activity feed | simple | M1 |
| Clock | `IClock` (`VirtualClock`) | cross-cutting | Virtual time; device code never reads wall-clock | simple | M1 |

Named extension points (mentioned only, deliberately NOT designed):
budget/quiet-hours gate, graceful-degradation harness.
