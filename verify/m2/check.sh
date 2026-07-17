#!/usr/bin/env bash
# M2 gate — Task Manager + the decompose altitude.
#
# Chains M1 (which chains M0). The earlier gates are permanent regression checks,
# not milestone trivia: gate 1 proves M2 changed nothing on the wire, gate 3
# proves the harness didn't re-learn a product literal.
#
# THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/m2/check.sh          # offline, no API key needed
#         ./verify/m2/check.sh --smoke  # + the LLM sims (needs a real key)
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m1/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 8 — the task lifecycle and the DAG sanitizer.
#
# Agent Board reports progress %, next step and pending counts as FACTS, so the
# ledger under them has to be sound: dependencies gate the frontier, illegal
# moves are refused rather than applied, a failed task is terminal but is NOT
# progress, and the percentage can never disagree with the "n/m" count.
#
# The DAG half guards the ledger from the decomposition, which is an LLM
# suggestion and can name unknown deps, self-depend, cycle, or run long. The
# cycle case is the dangerous one -- NextReady on a cycle returns nothing and the
# goal looks alive forever -- so it asserts a repaired cycle is RUNNABLE, not
# merely that it parsed.
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-task-lifecycle

echo "M2 gate: PASS"
