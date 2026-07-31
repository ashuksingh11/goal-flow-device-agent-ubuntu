#!/usr/bin/env bash
# M8 gate — RETIRED (v7.1). Kept as a link in the chain, not as a check.
#
# Chains M7 (→ M6 → M5 → M3 → M2 → M1 → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/m8/check.sh          # offline
#         ./verify/m8/check.sh --smoke  # + the LLM sims (needs a real key)
#
# WHAT WAS HERE: gate 18, which asserted the proactive-suggestion scan was
# deterministic and well-formed. v7.1 removed proactive suggestions outright — the
# device scan, the `suggestions`/`suggestion_action` frames, and the board's
# "Upcoming & Suggested" section — so there is nothing left to assert. The file stays
# because verify/v6-m2 chains it, and a broken chain is a worse thing to leave behind
# than an empty link.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m7/check.sh "$@"

echo "M8 gate: PASS (gate 18 retired with proactive suggestions in v7.1)"
