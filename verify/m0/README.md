# M0 gate

`./verify/m0/check.sh` proves the v3 restructure changed no behavior.
Run it before every commit in this milestone; `--smoke` adds the LLM check.

## Why goldens and not "diff the sim output"

The v3 design doc originally specified that `--simulate-week` / `--simulate-guest`
output stay "byte-identical". That is unsatisfiable: those runs make ~10
OpenRouter calls at temperature 0.1–0.2, so their text differs every run. What
IS deterministic is the kernel's advertised surface, and that's what gates 1–2
pin — captured from the code as it stood BEFORE the restructure (commit
`57f9232`), which is why the harness had to land first.

## The gates

| # | Pins | Catches |
|---|---|---|
| 1 | the `capabilities` frame, byte-for-byte | a changed tier, a dropped/renamed function, a lost description, a reordered module, an accidental steering rename (a CONTRACT change) |
| 2 | the grounding tool set: 13 functions, **in order** | a discovery rule that finds the wrong tools — or the right tools in the wrong order (this list is the tools array the model sees, so order is part of the prompt) |
| 3 | product-string count inside `Harness/` | a harness module learning a product literal |
| 4 | `--simulate-week` **shape**, not text | 7 days, `safety.gate == passed`, proposals from the known set, a quiet day, an adaptation |

## Gate 3 is "no NEW debt", not "zero debt"

`Harness/` does not have zero product strings today, and the design doc's claim
that it would was wrong on its own timeline: the doc defers the `IDomainObserver`
extraction to M2 and `policy.json` to M1, so those literals are *supposed* to
still be there when M0 ends. The honest invariant is that the count can only
shrink. Each remaining string carries a `// PRODUCT-DEBT(Mx)` marker naming the
milestone that clears it:

- **M1** — `SafetyPolicyEngine/SafetyFilter.cs`: hardcoded module/function names
  decide which check applies; ingredient groups are a C# table.
- **M2** — `TaskManager/MonitorAdapt.cs`: switches on `meal_plan`/`guest_dinner`,
  and the observers themselves are product knowledge in the generic core.

When a milestone clears debt, lower `harness-debt.count`. Never raise it.

## Gates are falsification-tested

A gate that cannot fail is worse than no gate — it is a false assurance. These
were checked by deliberately breaking things:

- dropping a grounding function → gate 2 fails
- weakening `ShoppingList.PlaceOrder` from `firm` to `light` → gate 1 fails
- stripping `[Unavailable]` from the stubs → 17 tools instead of 13 → gate 2 fails
- gate 4's assertions → verified against synthetic frames (6 days, an unknown
  proposal target, a blocked safety verdict all rejected)

That exercise found a real bug in the gate itself: `diff … && echo PASS` can
never fail, because `set -e` is suppressed for a command in a `&&` list unless
it is the last one. Every gate uses `if ! diff` for that reason — don't
"simplify" it back.
