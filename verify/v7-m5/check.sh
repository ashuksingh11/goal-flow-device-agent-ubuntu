#!/usr/bin/env bash
# v7-M5 gate — one goal changes another, and the plan says so without asking.
#
# Chains v7-M4 (→ v7-M3 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# The device half of the cross-goal moment is a control command that RE-PLANS AND APPLIES
# without an approval — the only path in the system that does. Gate 25 pins the two things
# that make that defensible: the account still owns the policy (the device re-arms from a
# block it was SENT and authors nothing), and a change already applied is never applied
# twice however many times the frame arrives.
#
# The cloud half lives in scripts/verify_crossgoal.py (gate 17) — blast radius,
# idempotence and self-retirement of the household window.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v7-m4/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

CROSS_DATA="$(mktemp -d)"
trap 'rm -rf "$CROSS_DATA"' EXIT
cp data/*.json "$CROSS_DATA"/
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-cross-goal --data "$CROSS_DATA"

echo "v7-M5 gate: PASS"
