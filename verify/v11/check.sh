#!/usr/bin/env bash
# v11 gate — A GOAL RETIRES ON ITS OWN DATES.
#
# Chains v9 (→ v8-M1 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# WHY IT EXISTS. A home-away card stayed on the Agent Board after its away days had
# passed — "sometimes". The sometimes was the whole clue: completion derived the goal's
# last day from the PLAN's own day span (window start + max `Day` - 1), and `Day` is an
# index the planner chooses with nothing tying it to the window's length.
#
# Measured across three identical runs of "I'll be out for Sunday and Monday" — a TWO-day
# window — the plans came back with day sets [1,2,4], [1,4] and [1,3]. So the derived last
# day landed one to three days past the away period, and the card sat on the board past
# its own dates until the clock caught up with a number the model had picked at random.
#
# Gate 34 pins the clamp (the window is a ceiling) AND the intent it must not break: a
# plan shorter than its window still completes when the plan does, a goal with no dates is
# never swept at all, and day 0 never resolves to a last day before the goal began.
#
# No API key, no network: ResolveLastDay is a pure function.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v9/check.sh

echo
echo "=== v11 gate 34: a goal retires on its own dates ==="
dotnet build GoalFlow.Device.csproj -v q --nologo > /dev/null
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-completion

echo "v11 gate: PASS"
