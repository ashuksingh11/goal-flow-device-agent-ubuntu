#!/usr/bin/env bash
# v7-M6 gate — the tick that follows the cross-goal moment must not undo it.
#
# Chains v7-M5 (→ v7-M4 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Gate 25 proves one goal can empty another's days. This one proves they STAY empty.
# The bug it exists for shipped in v7 and was found by a person, not by a gate: the
# family approved "we're away Sunday and Monday", the meal week marked both days away,
# and the next press of Advance day fired "the paneer spoiled" against Sunday — whose
# steer says "change tonight's dinner" — so the model cooked dinner on a day nobody is
# home. The headline of the demo, quietly undone by the very next interaction.
#
# Gate 26 pins both halves of the fix: the observer stops RAISING such a change as
# material, and the patch normaliser stops any patch LANDING on a skipped row whatever
# raised it — with the one exception that makes a cancelled trip recoverable.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v7-m5/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

AWAY_DATA="$(mktemp -d)"
trap 'rm -rf "$AWAY_DATA"' EXIT
cp data/*.json "$AWAY_DATA"/
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-away-immune --data "$AWAY_DATA"

echo "v7-M6 gate: PASS"
