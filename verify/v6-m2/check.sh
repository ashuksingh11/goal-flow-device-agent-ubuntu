#!/usr/bin/env bash
# v6-M2 gate — the constraints the cloud resolves per goal are actually ENFORCED here,
# and the device no longer keeps its own copy of the cap.
#
# Chains M8 (which chains M7 → M6 → M5 → M3 → M2 → M1 → M0). THE LATEST MILESTONE'S
# check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/v6-m2/check.sh          # offline, no API key needed
#         ./verify/v6-m2/check.sh --smoke  # + the LLM sims (needs a real key)
#
# What is new here is mostly INSIDE gate 6, which grew from 15 cases to 28: peak_hours
# (the existing time_window_block kind pointed at a new window — no engine code) and
# away_window (the new date_window_block kind). Half those rows assert what must NOT
# block: an 18:00 run on a goal that carries no peak window, and the departure- and
# return-day appliance runs, because the family is home for part of a travel day and
# "run the dishwasher before you leave" is the vacation plan's own best move.
#
# Gate 19 is the de-dup: the cap the planner is told about is the goal's ARMED cap,
# and data/budget.json no longer carries one. That bug was never a crash — the plan
# came back fine, just planned against $120 when the account had said $200.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m8/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 19 — one cap, and it comes from the account.
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-active-policy

echo "v6-M2 gate: PASS"
