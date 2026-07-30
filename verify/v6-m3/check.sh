#!/usr/bin/env bash
# v6-M3 gate — two goals share one wallet.
#
# Chains v6-M2 (→ M8 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/v6-m3/check.sh          # offline, no API key needed
#         ./verify/v6-m3/check.sh --smoke  # + the LLM sims (needs a real key)
#
# Per-goal caps cannot see each other: a $200 party and a $120 grocery week each fit
# their own ceiling and together blow a $600 month. Gate 20 proves the envelope closes
# that hole end to end — an approved order CONSUMES the household budget, another
# goal's ceiling falls when it is re-resolved, and that goal NOTICES (a material change
# with a steer, so it re-plans rather than discovering it at approval time).
#
# It also pins the two edges that look harmless: narrowing must be idempotent, and the
# ceiling must be able to CLIMB BACK when the envelope frees up — which only works
# because re-resolution starts from the dispatched block, never the last effective one.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v6-m2/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 20 — the envelope. MUTATES the world (it places an order), so it runs against a
# throwaway copy of data/ and never dirties the seed.
ENVELOPE_DATA="$(mktemp -d)"
trap 'rm -rf "$ENVELOPE_DATA"' EXIT
cp data/*.json "$ENVELOPE_DATA"/
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-envelope --data "$ENVELOPE_DATA"

# GATE 21 — the last gate before a real side effect reports a refusal AS a refusal.
# Side-effecting tools are not exposed during planning, so the window constraints can
# only bite at actuation; this pins that the user is told so. Mutates the world (the
# allowed proposal really runs), hence the throwaway dir.
APPROVAL_DATA="$(mktemp -d)"
trap 'rm -rf "$ENVELOPE_DATA" "$APPROVAL_DATA"' EXIT
cp data/*.json "$APPROVAL_DATA"/
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-approval-block --data "$APPROVAL_DATA"

echo "v6-M3 gate: PASS"
