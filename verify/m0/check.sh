#!/usr/bin/env bash
# M0 gate — proves the v3 restructure changed no behavior.
#
# There is no test framework in this repo and none is added. Instead this pins
# the parts of the agent that ARE deterministic. Note what is NOT checkable:
# --simulate-week output is LLM-driven (~10 OpenRouter calls at temperature
# 0.1-0.2), so its text differs every run — byte-diffing it is impossible.
# Gate 4 below covers it structurally instead.
#
# Usage:  ./verify/m0/check.sh          # gates 1-3 (offline, no API key needed)
#         ./verify/m0/check.sh --smoke  # + gate 4 (LLM; needs a real key)
set -euo pipefail
cd "$(dirname "$0")/../.."

dotnet build GoalFlow.Device.csproj -v q --nologo

# BuildKernel only configures the OpenRouter connector — it never calls out — so
# a placeholder key is enough to enumerate plugins.
OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}" \
  dotnet run --project GoalFlow.Device.csproj --no-build -- --dump-capabilities > /tmp/m0-dump.txt

# GATE 1 — the capabilities frame is byte-identical to the pre-refactor golden.
# Catches: a changed tier, a dropped/renamed function, a lost [Description],
# a reordered module, an accidental rename of a steering module (that last one
# is a CONTRACT change, reserved for M6).
# NB: every gate uses `if ! diff` and NOT `diff && echo PASS` — under `set -e`, a
# command in a && list does NOT trigger errexit unless it is the LAST one, so
# `diff && echo PASS` silently swallows the failure and the gate can never fail.
# (Verified: an intentionally dropped tool slipped through that form.)
if ! diff -u verify/m0/capabilities.golden.json <(head -1 /tmp/m0-dump.txt); then
  echo "gate 1 FAIL: the capabilities frame changed. If deliberate, this is a CONTRACT change." >&2
  exit 1
fi
echo "gate 1 (capabilities frame): PASS"

# GATE 2 — the grounding tool set is byte-identical, SAME ORDER, exactly 13.
# Order matters: this list IS the tools array handed to the model.
# 13 = the read functions of the 7 IMPLEMENTED plugins. A naive "all
# non-side-effecting functions" rule yields 17 — FamilyProfiles and Budget are
# reads too, they just throw — so [Unavailable] stub exclusion is load-bearing.
if ! diff -u verify/m0/grounding.golden.txt <(tail -n +2 /tmp/m0-dump.txt); then
  echo "gate 2 FAIL: the planner's tool set changed (content or ORDER)." >&2
  exit 1
fi
test "$(wc -l < verify/m0/grounding.golden.txt)" -eq 13
echo "gate 2 (grounding set, 13 fns in order): PASS"

# GATE 3 — product-string debt inside Harness/ can only shrink.
# The "Harness/ has zero product strings" invariant is FALSE on day one by
# design: the design doc defers the IDomainObserver extraction to M2 and
# policy.json to M1. So the honest invariant is "no NEW product strings" —
# this pins the count. Lower it when a milestone clears debt; never raise it.
#
# COMMENTS ARE STRIPPED FIRST. The invariant is about a harness module DEPENDING
# on a product literal; prose explaining why the design is the way it is does not
# couple anything, and counting it would price honest comments as debt and reward
# deleting them. (M1 made this concrete: moving the checks into policy.json left
# behind doc comments saying "this used to hardcode ShoppingList" — the coupling
# was gone but the count hadn't moved.) Stripping only ever UNDER-counts prose;
# real code coupling still trips it.
if [ -d src/GoalFlow.Device/Harness ] && [ -f verify/m0/harness-debt.count ]; then
  debt=$(find src/GoalFlow.Device/Harness -name '*.cs' -exec sed -E 's;//.*$;;' {} + \
         | grep -Eo 'meal_plan|guest_dinner|ShoppingList|PlaceOrder|Recipes|Appliance|Notify|Inventory|Calendar|Reminders|Guests' | wc -l)
  pinned=$(cat verify/m0/harness-debt.count)
  if [ "$debt" -gt "$pinned" ]; then
    echo "gate 3 FAIL: product strings in Harness/ rose to $debt (pinned $pinned)." >&2
    echo "  A harness module must not learn a product literal. See PRODUCT-DEBT markers." >&2
    exit 1
  fi
  if [ "$debt" -lt "$pinned" ]; then
    echo "gate 3: debt fell to $debt (pinned $pinned) — lower verify/m0/harness-debt.count."
  fi
  echo "gate 3 (harness product-string debt = $debt): PASS"
fi

# GATE 4 — LLM smoke, structural not byte-wise. Opt-in: needs a real key and
# burns tokens. Asserts the SHAPE the sims must keep, not their wording.
if [ "${1:-}" = "--smoke" ]; then
  dotnet run --project GoalFlow.Device.csproj --no-build -- --simulate-week > /tmp/m0-week.jsonl
  head -1 /tmp/m0-week.jsonl | python3 -c '
import json,sys
p = json.load(sys.stdin)
assert p["type"] == "plan_ready", p["type"]
plan = p["payload"]["plan"]
assert len(plan) == 7, f"expected 7 dinners, got {len(plan)}"
assert [i["day"] for i in plan] == [1,2,3,4,5,6,7], [i["day"] for i in plan]
assert p["payload"]["safety"]["gate"] == "passed", p["payload"]["safety"]
known = {"ShoppingList.Add","ShoppingList.Remove","ShoppingList.PlaceOrder","Appliance.PreheatOven",
         "Appliance.RunProgram","Appliance.Defrost","Reminders.Create","Reminders.Delete",
         "Calendar.AddEvent","Inventory.ConsumeItem"}
got = {x["module"] + "." + x["function"] for x in p["payload"]["proposals"]}
assert got <= known, f"unknown proposal targets: {got - known}"
print("gate 4 (simulate-week shape): PASS")'
  grep -q '"task_status":"monitoring"' /tmp/m0-week.jsonl || { echo "gate 4 FAIL: no quiet monitoring day" >&2; exit 1; }
  grep -q '"tier":"adapt"' /tmp/m0-week.jsonl || { echo "gate 4 FAIL: adaptation never fired" >&2; exit 1; }
fi

echo "M0 gate: PASS"
