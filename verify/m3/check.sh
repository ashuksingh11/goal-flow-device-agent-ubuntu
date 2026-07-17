#!/usr/bin/env bash
# M3 gate — the Pre-check Engine.
#
# Chains M2 (which chains M1, M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/m3/check.sh          # offline, no API key needed
#         ./verify/m3/check.sh --smoke  # + the LLM sims (needs a real key)
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m2/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 9 — is the world ready?
#
# Runs against a THROWAWAY copy of data/, because it flips device_state flags to
# force each outcome and must never dirty the seed.
#
# The failure paths are the point. A gate that only ever sees a healthy world
# proves nothing: passing is also exactly what a probe that does nothing does.
# (Verified — a stubbed always-pass probe trips 4 of these.) So it checks the
# goal gate blocking, the parameterized appliance probe, that an offline OVEN
# does not block an unrelated ORDER, that a module-wide binding is a floor, and
# that everything recovers — because a precheck says "not yet", not "never".
PRECHECK_DATA="$(mktemp -d)"
trap 'rm -rf "$PRECHECK_DATA"' EXIT
cp data/*.json "$PRECHECK_DATA"/
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-prechecks --data "$PRECHECK_DATA"

echo "M3 gate: PASS"
