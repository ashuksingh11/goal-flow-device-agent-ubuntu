# Mock world (`data/`)

JSON files standing in for the Family Hub's sensors/actuators during development.
The SK capability plugins read and write them through `MockWorldStore`.

## The generic-clock rule (hard invariant)

**No absolute dates anywhere in mock data or code.** "Today" comes from `IClock`
(`Modules/Steering/Clock.cs`): the real system date by default (`SystemClock`),
or a simulated date driven by `control` frames / `--date` (`SimulatedClock`,
`set_date` + `advance_day`). Everything else is stored **relative**:

| File | Relative field | Meaning |
|---|---|---|
| `inventory.json` | `expires_in_days` | item expires `today + N` (null = non-perishable) |
| `calendar.json` | `day_offset` | event occurs on `today + N` |
| `reminders.json` | `due_in_days` | reminder due `today + N` |
| `sample-contract.json` | `${today+N}` tokens | resolved to ISO dates when the file is loaded |

`MockWorldStore.LoadResolvedAsync` resolves offsets against `IClock.Today` at
**read time**, so the same seed data is always "this week" — whether run today,
next month, or under a simulated clock in a demo. `control: reset` restores the
pristine seeds.

## Files

- `inventory.json`, `recipes.json` — meal-domain world (fridge contents, recipe box)
- `calendar.json`, `reminders.json`, `shopping_list.json` — shared world
- `sample-contract.json` — a v2 `dispatch` (generic Task Contract, domain `meal_plan`)
- `sample-approval.json` — a v2 `approval` frame for the sample contract
- Later milestones add `family.json`, `budget.json`, `appliances.json`, `guests.json`
  (guest_dinner domain).
