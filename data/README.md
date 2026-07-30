# Mock world (`data/`)

JSON files standing in for the Family Hub's sensors/actuators during development.
The SK capability plugins read and write them through `MockFamilyHubAdapter`.

## The generic-clock rule (hard invariant)

**No absolute dates anywhere in mock data or code.** "Today" comes from `IClock`
(`Harness/Clock/Clock.cs`): the real system date by default (`SystemClock`),
or a simulated date driven by `control` frames / `--date` (`SimulatedClock`,
`set_date` + `advance_day`). Everything else is stored **relative**:

| File | Relative field | Meaning |
|---|---|---|
| `inventory.json` | `expires_in_days` | item expires `today + N` (null = non-perishable) |
| `calendar.json` | `day_offset` | event occurs on `today + N` |
| `reminders.json` | `due_in_days` | reminder due `today + N` |
| `deliveries.json` | `next_in_days` | the next drop arrives `today + N` (`every_days` is a cadence, not a date) |
| `daily_events.json` | `day_offset` | resolves each demo event to the fixed week's target ISO date at snapshot time (does not decide when it fires — the presenter does, via `control: trigger_event`) |
| `workout.json` | `day_offset` (negative) | a logged activity day, `today − N`; `-1` is always yesterday |
| `sample-contract.json` | `${today+N}` tokens | resolved to ISO dates when the file is loaded |

`MockFamilyHubAdapter.LoadResolvedAsync` resolves offsets against `IClock.Today` at
**read time**, so the same seed data is always "this week" — whether run today,
next month, or under a simulated clock in a demo. `control: reset` restores the
pristine seeds.

## Adding a NEW data file — the seeding trap

`EnsureDataDir` (Program.cs) seeds a `--data <dir>` from `data/` by copying **all**
`*.json`, but it **skips seeding entirely if the target dir already contains any json**
("already has a world"). So when you add a new file here, every pre-existing `--data`
dir (`data-a/`, `data-b/`, a demo dir) will **silently lack it** — the observer that
reads it hits `FileNotFoundException`, returns no changes, and the goal just never
adapts. Nothing errors. **Delete the dir and let it re-seed** after adding a file.

## Files, by demo use case

The five headline use cases (v3.4), plus `guest_dinner` which is kept but unheadlined:

| Domain | Its slice | Change source (day-triggered) |
|---|---|---|
| `meal_plan` | `inventory`, `recipes`, `calendar`, `workout` | `daily_events.json` |
| `birthday_party` | `party`, `family`, `budget`, `calendar` | `party.pending_updates` |
| `vacation_prep` | `vacation`, `security`, `appliances`, `deliveries`, `calendar` | `vacation.pending_updates` |
| `grocery_cost` | `grocery`, `inventory`, `budget` | `grocery.pending_updates` |
| `energy_saving` | `energy`, `appliances` | `energy.pending_updates` |
| `guest_dinner` | `guests`, `calendar`, `recipes` | `guests.pending_updates` |

- `inventory.json` — deliberately serves THREE goals at once: items expiring within 3
  days (meal/waste), milk below its `restock_threshold` (grocery), and those same
  perishables being what must be eaten before departure (vacation). Keep
  `restock_threshold` in the item's OWN unit — it is compared raw against `quantity`.
- `grocery.json` — watchlist (priced against `budget.json`'s book, the authoritative
  one), current `offers`, and a `pending_updates` script. No plugin: `Budget.EstimateCost`
  prices a basket and `ShoppingList.PlaceOrder` is firm-tier + capped by `numeric_cap`.
- `energy.json` — baseline kWh, the `reduce_percent` target, peak/off-peak tariff, and a
  `pending_updates` script. Also plugin-less: it acts through `Appliance.RunProgram`
  (eco program, off-peak), `Reminders` and `Notify`. Per-appliance draw lives on
  `appliances.json` under `energy`, which `Appliance.ListAppliances` returns **verbatim**
  — that is how the planner sees real kWh without a new tool.
- `daily_events.json` — the meal week's world-change feed: 7 events (`day1-activity` …
  `day6-lighter-sunday`) with `id`/`label`/`title`/`order`/`day`/`kind`/`summary`/
  `context`/`steer`. **`kind` is a CLOSED vocabulary** — `MealPlanObserver.IsMaterial`
  decides; anything unlisted fires but is silently non-material.
  **Day 1 is two events on purpose (v7):** `day1-activity` is *informational* (listed in
  the day summary, no steer, never opens an approval) and `day1-fish` is *material*, with
  a steer that quotes the activity numbers — so two changes produce ONE adaptation that
  can explain both. An entry with no `steer` must also be non-material, or it re-plans
  against nothing.
- `deliveries.json` — standing subscriptions and scheduled drops, read by
  `Deliveries.ListDeliveries` and mutated by `Hold`/`Resume`. Its own file rather than a
  field on `shopping_list.json`: a shopping list is what the family INTENDS to buy, these
  are commitments already made to third parties, and holding one is an outward-facing act
  with a date attached — which is why `Hold` is graded **A2** and lands as its own
  approval. **`essential` is the household's call, recorded not inferred**: the repeat
  prescription is marked essential and `Hold` REFUSES it outright rather than trusting the
  planner to have read the note. A plan that pauses someone's medication to tidy up the
  porch is not something anyone should have to catch by reading.
- `workout.json` — the household's training routine and the last few days of steps and
  calories, read by `Workout.GetWeeklyRoutine` / `GetRecentActivity`. It is the evidence
  behind the cloud's soft *workout-friendly* preference on meal goals. **There is
  deliberately no `WorkoutObserver` and no `workout_routine` domain**: an observer is what
  makes the cloud advertise a domain, so adding one would start routing training goals to
  a device that cannot advance them. Activity is a fact the meal planner reasons over.
  The Act 2 spike lives in `daily_events.json`, **not** here — seeded here it would
  already be true on day 1.
- `appliances.json` — the SmartThings appliance world: oven, dishwasher, washer, fridge,
  TV and (v7) the **robot vacuum** that return-readiness schedules through the existing
  `Appliance.RunProgram`. **There is still no thermostat.** The plugin's `[Description]`
  claimed a vacuum from v2 while the seed held none, which is not a harmless comment — the
  planner reads that description, so an over-claim invites a step the device cannot run.
- `family.json`, `budget.json` — grounding + the price book. Hard health constraints
  arrive on the dispatch's `constraints.hard`, NOT from `family.json`.
- `device_state.json` — the precheck world (flip a flag to break a goal on cue).
- `calendar.json`, `reminders.json`, `shopping_list.json`, `notifications.json`,
  `security.json` — shared world; several are written at runtime (see below).
- `sample-contract.json` / `-guest.json`, `sample-approval.json` — CLI test inputs.

## Runtime-mutated files (reset before a demo)

`shopping_list.json`, `appliances.json` (`scheduled_actions`), `notifications.json`,
`inventory.json`, `calendar.json`, `reminders.json`, `security.json`,
`deliveries.json` (`held`, `held_until`) accumulate state
during a run. `control: reset` restores the seed captured **at process start**, so for a
clean recording restore from git *before* launching, not after.
