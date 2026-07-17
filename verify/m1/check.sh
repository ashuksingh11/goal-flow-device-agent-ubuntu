#!/usr/bin/env bash
# M1 gate — the Safety Policy Engine.
#
# Runs the M0 gates first (they are permanent regression gates, not M0 trivia:
# gate 1 proves M1 changed nothing on the wire, gate 3 proves the harness didn't
# re-learn a product literal), then M1's own.
#
# THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN. Each chains the previous.
#
# Usage:  ./verify/m1/check.sh          # offline, no API key needed
#         ./verify/m1/check.sh --smoke  # + the LLM sims (needs a real key)
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m0/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"
run() { dotnet run --project GoalFlow.Device.csproj --no-build -- "$1"; }

# GATE 5 — per-goal policy isolation. The regression test for a live safety bug:
# the armed policy was one field on a singleton, so a second goal overwrote the
# first's constraints mid-plan and the gate enforced the wrong family's
# allergens. Two goals run concurrently with different constraints, both armed
# before either checks, so a shared field cannot pass.
run --verify-policy-isolation

# GATE 6 — the declarative rules block what they must, and ALLOW what they must
# not block. The over-blocking cases matter as much as the under-blocking ones:
# a nut allergy that vetoes coconut and butternut squash gets the agent switched
# off. Includes the M1 fix (allergens ["peanuts"] now blocks "peanut butter",
# which v2's substring match did not).
run --verify-safety-rules

# GATE 7 — grades: the ratchet (config may only make a grade STRICTER; weakening
# is fatal at startup) and AX (never proposable, blocked before constraints are
# consulted). AX has no real subject until M7's smart lock, so it is exercised
# through a throwaway policy rather than left unrun until the demo.
run --verify-grades

echo "M1 gate: PASS"
