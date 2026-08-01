#!/usr/bin/env bash
# v7-M2 gate — one Advance day TELLS the family two things and ASKS about one.
#
# Chains v6-M3 (→ v6-M2 → M8 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/v7-m2/check.sh          # offline, no API key needed
#         ./verify/v7-m2/check.sh --smoke  # + the LLM sims (needs a real key)
#
# Act 2 shows two overnight changes — a hard training day and a fish delivery — and one
# approval. That is a real distinction in the harness rather than staging, and before v7
# only half of it existed: a non-material change was observed and then dropped, so it was
# indistinguishable from a quiet day. Gate 22 pins both halves — the informational change
# is surfaced and carries no steer, the material one carries a steer that quotes the
# other one's numbers, so the single re-plan the user approves can explain itself in full.
#
# It also pins the two seeding traps that fail SILENTLY: a missing data/workout.json makes
# the observer return nothing and the goal simply never adapt, and seeding the activity
# spike into workout.json's steady state would make day 1 already know about it — the
# adaptation would still fire, and it would be reacting to nothing new.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v6-m3/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 22 — read-only: it observes, it does not adapt or execute, so it runs against the
# repo's own data/ without dirtying it.
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-day-tick

echo "v7-M2 gate: PASS"
