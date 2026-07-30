#!/usr/bin/env bash
# v7-M4 gate — the home-away goal can reach what its plan promises.
#
# Chains v7-M3 (→ v7-M2 → v6-M3 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/v7-m4/check.sh          # offline, no API key needed
#         ./verify/v7-m4/check.sh --smoke  # + the LLM sims (needs a real key)
#
# A plan step is only real if a function exists behind it. Act 3 narrates pausing
# deliveries, handing the house to SmartThings, arming security and coming back to a clean
# house — gate 24 pins that each of those is callable, correctly graded, and that the
# return half works, because a hold that cannot be resumed is a subscription quietly
# killed rather than a trip well planned.
#
# THE ROW THAT MATTERS MOST IS THE REFUSAL. "Pause non-essential deliveries" has one
# obvious way to go wrong, and it is not caught by reading the plan: it is caught by
# Deliveries.Hold saying no to the repeat prescription. Deterministic code, the same shape
# as the Safety engine, for the same reason — nobody should have to notice that one.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v7-m3/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 24 — MUTATES the world (it really holds and resumes a delivery, and schedules a
# clean), so it runs against a throwaway copy and never dirties the seed.
AWAY_DATA="$(mktemp -d)"
trap 'rm -rf "$AWAY_DATA"' EXIT
cp data/*.json "$AWAY_DATA"/
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-away-capabilities --data "$AWAY_DATA"

echo "v7-M4 gate: PASS"
