#!/usr/bin/env bash
# M5 gate — concurrency + persistence.
#
# Chains M3 (which chains M2, M1, M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/m5/check.sh          # offline
#         ./verify/m5/check.sh --smoke  # + the LLM sims (needs a real key)
#
# NOTE: the CLOUD half of M5 (per-goal locks, the SQLite checkpointer) is gated in
# the cloud repo — `python scripts/verify_persistence.py`, which needs a real key.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m3/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 11 — trace isolation under concurrency.
#
# Every agent_event carries a goal_id and a seq, and the UI DROPS any frame whose
# seq isn't greater than the last it saw for that goal. So a shared counter is not
# cosmetic: goal B starting reset seq to 0 and re-pinned the id, and goal A's
# remaining events went out under B's id with a seq that had gone BACKWARDS — the
# UI discarded them and A's plan stopped appearing, with no error anywhere.
#
# The barrier inside is a two-way rendezvous, deliberately. With a single
# TaskCompletionSource, `await` on an already-completed task continues
# SYNCHRONOUSLY: goal-a ran to completion before goal-b started, the two never
# overlapped, and a deliberately shared scope PASSED. A concurrency test that does
# not actually interleave is theatre — verified by re-running the falsification
# after the fix (goal-a: 0 frames, goal-b: 20).
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-trace-isolation

echo "M5 gate: PASS"
