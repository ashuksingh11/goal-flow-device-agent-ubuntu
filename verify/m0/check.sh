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
#
# IT ALSO CATCHES A CHANGED DOMAIN HINT, which is the one people are surprised by. A hint
# reads like a comment and is not: the cloud's interpreter CLASSIFIES against it, so
# editing one changes which goals route where — and, since the hints are the whole of what
# the interpreter believes is possible, whether a goal is refused at all. v7 rewrote all
# six to draw their edges ("getting the HOUSE ready … booking the TRIP is NOT this"),
# which is why the demo's out-of-domain closer lands.
#
# v7.1 REGENERATED THIS GOLDEN for exactly one line: Recipes.FindRecipes' description.
# The tool set and its order are untouched (gate 2 still diffs clean) — only what the
# model is TOLD about that one function changed, from "finds recipes, optionally
# filtered…" to "returns the ENTIRE box (one call is enough)". That wording is
# behaviour: the old phrasing advertised filtering as the way to use the tool, and the
# model duly filtered ten-plus times per plan. See verify/v7-m7 and gate 28.
#
# v9 REGENERATED IT AGAIN, same line, and this time only for LENGTH — the v7.1 sentence
# had grown to 60 words of shouting. Every load-bearing clause survives verbatim in
# meaning: call once, never follow with GetRecipe, preferTags only re-orders. Nothing
# else in the frame moved (the diff was that one string). If a future edit drops one of
# those three clauses, gate 28 is the one that will notice, not this one.
#
# v12.2 REGENERATED IT A THIRD TIME, and it is the same line again. FindRecipes no longer
# returns the whole box unconditionally: a recipe whose `requires_fresh` ingredient is not
# in the fridge is withheld, and the reply reports the count and the reason. The sentence
# had to stop saying "the whole recipe box — every recipe", because that is now false, and
# a tool description that overstates what it returns is the v7.1 lie in a new form. All
# three load-bearing clauses survive verbatim. The reason for the withhold is the demo's
# day-2 fish swap: see data/recipes.json rcp-007 and gate 28 half one-b.
# NB: every gate uses `if ! diff` and NOT `diff && echo PASS` — under `set -e`, a
# command in a && list does NOT trigger errexit unless it is the LAST one, so
# `diff && echo PASS` silently swallows the failure and the gate can never fail.
# (Verified: an intentionally dropped tool slipped through that form.)
if ! diff -u verify/m0/capabilities.golden.json <(head -1 /tmp/m0-dump.txt); then
  echo "gate 1 FAIL: the capabilities frame changed. If deliberate, this is a CONTRACT change." >&2
  exit 1
fi
echo "gate 1 (capabilities frame): PASS"

# GATE 2 — the grounding tool set is byte-identical, SAME ORDER, exactly 21.
# Order matters: this list IS the tools array handed to the model.
# Was 13 through M6 (the read functions of the 7 implemented plugins). M7 grew it:
#   +4  implemented the FamilyProfiles and Budget stubs (GetProfiles, GetMember,
#       GetBudgetStatus, EstimateCost — reads that used to throw)
#   +1  the new Security plugin's GetSecurityStatus read
# so 13 -> 18. Notify was also implemented but its functions are side-effecting, so
# they are proposable actions, not grounding tools. v7 added:
#   +2  the Workout plugin's GetWeeklyRoutine and GetRecentActivity — the evidence
#       behind the cloud's soft "workout-friendly" preference on meal goals
# so 18 -> 20, both APPENDED. v7-M4 added one more:
#   +1  Deliveries.ListDeliveries — Hold and Resume are side-effecting, so they are
#       proposable actions rather than grounding tools (the same split as Notify)
# so 20 -> 21. That matters and it is why the diff is worth reading
# rather than regenerating: Workout belongs next to FamilyProfiles conceptually, and
# putting it there would have shifted every tool after it. This list is the order the
# model sees, so a reorder is a behaviour change dressed as tidying.
# The stub-exclusion mechanism is still load-bearing for anything that stays
# [Unavailable]; this golden IS the reviewable record of the catalog change.
if ! diff -u verify/m0/grounding.golden.txt <(tail -n +2 /tmp/m0-dump.txt); then
  echo "gate 2 FAIL: the planner's tool set changed (content or ORDER)." >&2
  exit 1
fi
test "$(wc -l < verify/m0/grounding.golden.txt)" -eq 21
echo "gate 2 (grounding set, 21 fns in order): PASS"

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
  # The vocabulary is CASE-INSENSITIVE and covers three ways product knowledge
  # leaks in, not just one. The first version listed capitalized module names
  # only and reported 4 while the real figure was 17: MonitorAdapt does not
  # merely switch on domain names, it knows the product's DATA MODEL — which
  # documents exist ("calendar", "daily_events") and their shapes
  # ("guests.pending_updates") — none of which the old pattern could see.
  #   1. domains        meal_plan, guest_dinner
  #   2. module names   ShoppingList, Appliance, …  (any case)
  #   3. resource names calendar, daily_events, …   (the pack's documents)
  #   4. change kinds   guest.*, inventory.*, appliance.*, meal.*
  debt=$(find src/GoalFlow.Device/Harness -name '*.cs' -exec sed -E 's;//.*$;;' {} + \
         | grep -Eoi 'meal_plan|guest_dinner|daily_events|shopping_list|shoppinglist|placeorder|pending_updates|inventory|calendar|recipes|appliances?|notify|reminders|guests?|"(guest|inventory|appliance|meal)\.[a-z_]+"' \
         | wc -l)
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
# The COMPLETE set of [SideEffect] functions across the FamilyHub plugins (14). Kept in
# sync with the proposal catalog in GoalAgent.BuildPlanningInstruction — the gate proves
# the planner never invents a target outside what the device can actually execute.
known = {"ShoppingList.Add","ShoppingList.Remove","ShoppingList.PlaceOrder","Appliance.PreheatOven",
         "Appliance.RunProgram","Appliance.Defrost","Reminders.Create","Reminders.Delete",
         "Calendar.AddEvent","Inventory.ConsumeItem","Security.LockAllDoors","Security.ArmSecurity",
         "Notify.SendNotification","Notify.Announce"}
got = {x["module"] + "." + x["function"] for x in p["payload"]["proposals"]}
assert got <= known, f"unknown proposal targets: {got - known}"
print("gate 4 (simulate-week shape): PASS")'
  grep -q '"task_status":"monitoring"' /tmp/m0-week.jsonl || { echo "gate 4 FAIL: no quiet monitoring day" >&2; exit 1; }
  grep -q '"tier":"adapt"' /tmp/m0-week.jsonl || { echo "gate 4 FAIL: adaptation never fired" >&2; exit 1; }
fi

echo "M0 gate: PASS"
