#!/usr/bin/env bash
# M8 gate — proactive suggestions + negative-path honesty.
#
# Chains M7 (→ M6 → M5 → M3 → M2 → M1 → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/m8/check.sh          # offline
#         ./verify/m8/check.sh --smoke  # + the LLM sims (needs a real key)
#
# The CLOUD half of M8 is gated in the cloud repo and needs no key:
#   python scripts/verify_board.py     # gate 13 — the fold, incl. precheck→Waiting
#   python scripts/verify_mirrors.py   # gate 14 — mirrors, now with the suggestion frames
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m7/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 18 — the proactive scan is deterministic and well-formed.
#
# A suggestion is the one thing the device raises with no goal in flight, and the
# board shows it before anything is asked. It has to be REPEATABLE — the same
# suggestions every run — or a demo flickers; that repeatability is the whole reason
# the suggester is a scan and not an LLM, and this gate is what pins it. It also
# asserts every suggestion is acceptable into a goal (a non-empty goal_text), because
# an empty one would submit a blank user_goal.
dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-suggestions

echo "M8 gate: PASS"
