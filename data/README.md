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
| `daily_events.json` | `day_offset` | resolves each demo event to the fixed week's target ISO date at snapshot time (does not decide when it fires — the presenter does, via `control: trigger_event`) |
| `sample-contract.json` | `${today+N}` tokens | resolved to ISO dates when the file is loaded |

`MockFamilyHubAdapter.LoadResolvedAsync` resolves offsets against `IClock.Today` at
**read time**, so the same seed data is always "this week" — whether run today,
next month, or under a simulated clock in a demo. `control: reset` restores the
pristine seeds.

## Files

- `inventory.json`, `recipes.json` — meal-domain world (fridge contents, recipe box)
- `calendar.json`, `reminders.json`, `shopping_list.json` — shared world
- `appliances.json`, `guests.json` — guest_dinner domain world (appliance status, guest
  list + `pending_updates` for RSVP/late-arrival adaptation)
- `daily_events.json` — the presenter-fired event catalog for the event-driven meal
  demo: 6 events (`day1-restock`, `day2-shortage`, `day3-football`, `day4-guest`,
  `day5-oven`, `day6-lighter-sunday`), each with `id`/`label`/`title`/`order`/`day`/
  `kind`/`summary`/`context`/`steer`. The UI shows them as `demo_events` chips
  (from `plan_ready.payload.demo_events`); firing one sends `control: trigger_event`
  with its `event_id`, which runs one scoped LLM re-plan against that event's
  `context` + `steer` against the plan item on that `day`, deduped once per id.
- `sample-contract.json` — a v2 `dispatch` (generic Task Contract, domain `meal_plan`)
- `sample-approval.json` — a v2 `approval` frame for the sample contract
- Later milestones add `family.json`, `budget.json`.
