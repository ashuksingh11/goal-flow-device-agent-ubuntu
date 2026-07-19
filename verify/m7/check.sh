#!/usr/bin/env bash
# M7 gate — the new use cases and plugins are actually wired.
#
# Chains M6 (which chains M5 → M3 → M2 → M1 → M0). THE LATEST MILESTONE'S check.sh
# IS THE ONE TO RUN.
#
# Usage:  ./verify/m7/check.sh          # offline
#         ./verify/m7/check.sh --smoke  # + the LLM sims (needs a real key)
#
# The heavy lifting is already done by the chained M0 gates: gate 1 byte-diffs the
# whole capabilities frame (which INCLUDES the advertised domains), and gate 2
# byte-diffs the grounding tool set (which proves the implemented stubs' reads joined
# it). This gate is the human-readable statement of the M7 deliverable on top of that:
# the four domains route, and the plugins a demo needs are present and available.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m6/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 17 — the M7 use cases and plugins are advertised.
#
# A use case is only real if the device advertises a domain the cloud can route to
# (the observer IS that advertisement) and the plugins it needs are AVAILABLE, not
# [Unavailable] stubs. v2 declined "get the house ready, we're away" outright; the
# proof it can now is vacation_prep being in this list.
dotnet run --project GoalFlow.Device.csproj --no-build -- --dump-capabilities 2>/dev/null > /tmp/m7-dump.txt
# The frame is read from a FILE, not stdin: `python3 - <<'PY'` already binds stdin to
# the heredoc (the script itself), so a piped `head -1 | python3 -` would hand json a
# consumed, empty stdin and crash — which would look nothing like the check it claims
# to run. Pass the path as argv instead.
head -1 /tmp/m7-dump.txt > /tmp/m7-frame.json
python3 - /tmp/m7-frame.json <<'PY'
import json, sys

frame = json.load(open(sys.argv[1]))

# v3.4 added grocery_cost + energy_saving — the five headline demo use cases plus
# guest_dinner. Set equality on purpose: a domain that stops being advertised is a
# use case that silently stops routing.
expected_domains = {
    "birthday_party", "energy_saving", "grocery_cost",
    "guest_dinner", "meal_plan", "vacation_prep",
}
got_domains = {d["id"] for d in frame.get("domains", [])}
if got_domains != expected_domains:
    print(f"  FAIL domains advertised {sorted(got_domains)} != expected {sorted(expected_domains)}")
    sys.exit(1)
print(f"  ok   six domains route: {sorted(got_domains)}")

# The plugins M7 needs must be present AS CAPABILITIES (not steering) — meaning
# available, since an [Unavailable] plugin still lists in the frame but its reads are
# withheld from grounding (gate 2 catches that half). We assert presence here and let
# gate 2 assert the grounding side.
modules = {m["name"] for m in frame.get("modules", []) if m.get("kind") == "capability"}
need = {"FamilyProfiles", "Budget", "Notify", "Security"}
missing = need - modules
if missing:
    print(f"  FAIL these M7 plugins are not advertised as capabilities: {sorted(missing)}")
    sys.exit(1)
print(f"  ok   M7 plugins present: {sorted(need)}")
print("gate 17 (M7 use cases + plugins): PASS")
PY

echo "M7 gate: PASS"
