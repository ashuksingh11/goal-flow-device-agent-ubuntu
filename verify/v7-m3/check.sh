#!/usr/bin/env bash
# v7-M3 gate — the run SAYS what it did, and a v6 client cannot tell the difference.
#
# Chains v7-M2 (→ v6-M3 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/v7-m3/check.sh          # offline, no API key needed
#         ./verify/v7-m3/check.sh --smoke  # + the LLM sims (needs a real key)
#
# Through v6 the composing screen was BLANK for the longest stretch of the run. That was
# not a bug: the compose call is not streamed and keeps its plan JSON off the thinking
# channel deliberately, so the planner emitted nothing at all on a healthy run — and a
# silent engine is indistinguishable from a broken one. It cannot narrate what it is
# thinking, but it can say what it is thinking AGAINST, and gate 23 pins the shape that
# makes that possible.
#
# The assertion worth having is the BACK-COMPAT one. The whole design rests on `text`
# staying the only required field, so a surface that ignores kind/step/detail renders
# exactly what it always did — and the cheapest way to break that is to start stamping
# `kind` on plain narration for tidiness. The gate fails if anything does.
#
# It also pins that a SAFETY BLOCK is said out loud. Until v7 the most interesting thing
# that engine ever does reached the user as a chip summary and a number on the plan card,
# and never as a sentence at the moment it happened.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v7-m2/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 23 — read-only: it captures its own trace and blocks one call that never runs.
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-thinking-steps

echo "v7-M3 gate: PASS"
