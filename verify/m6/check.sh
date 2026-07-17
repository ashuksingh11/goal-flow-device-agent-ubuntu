#!/usr/bin/env bash
# M6 gate — the board's truth, and the hang that made it a lie.
#
# Chains M5 (which chains M3 → M2 → M1 → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Usage:  ./verify/m6/check.sh          # offline
#         ./verify/m6/check.sh --smoke  # + the LLM sims (needs a real key)
#
# NOTE: the CLOUD half of M6 is gated in the cloud repo and needs no key:
#   python scripts/verify_board.py      # gate 13 — the board fold
#   python scripts/verify_mirrors.py    # gate 14 — mirrors, allowlists, field enums
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/m5/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

# GATE 15 — a stalled provider stream must not wedge a goal.
#
# Observed twice in one session: the stream delivered tokens, stopped mid-JSON, and
# never returned. The process stayed alive, nothing was logged, and every surface —
# chat rail, Agent Board card — kept reporting "Working out the steps…" for FOUR HOURS.
# The provider was healthy; OpenRouter had routed that stream to one that hung.
#
# HttpClient.Timeout does not cover this: streaming reads with ResponseHeadersRead, so
# the timeout is satisfied the moment headers arrive and the body read is unbounded.
# The fix is a per-call deadline on a LINKED token, which expires without touching the
# goal's own token — so it classifies as transient and flows into the retry machinery
# that already existed for provider flakiness.
#
# The gate runs a real IChatCompletionService against a real socket that serves valid
# SSE and then goes silent, because the claim under test is a claim about the SDK.
#
# THE `timeout` IS PART OF THE GATE. Without the deadline this verification HANGS —
# proven by falsification — and a gate that hangs on a regression is barely better than
# one that passes. 120s is ~3x the honest runtime (the internal deadline is 3s).
#
# Falsified three ways:
#   * deadline removed          -> hangs (the bug, reproduced); killed by this timeout.
#   * fixture the SDK rejects   -> FAIL: "the DEADLINE ended the stream, not a parse error".
#   * (v1 of this gate had exactly that second bug: an unescaped quote made the chunk
#     invalid JSON, the SDK threw JsonReaderException in 87ms, JsonException is itself
#     in the transient list, and every loose assertion passed. It proved nothing. The
#     lower time bound is what makes the gate real: only waiting out the deadline can
#     satisfy it.)
timeout 120 dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-deadline

# GATE 16 — every provider call is actually WIRED to a deadline.
#
# Gate 15 proves the mechanism; this proves it is used. They are different claims, and
# the mechanism working while a call site is left on the bare token is exactly how this
# regresses — silently, back into a four-hour hang.
echo "  checking every chat call site carries a deadline…"
BARE=$(grep -nE "chat\.Get(Streaming)?ChatMessageContents?Async\([^)]*_kernel, ct\)" \
    src/GoalFlow.Device/Agent/GoalAgent.cs || true)
if [ -n "$BARE" ]; then
    echo "  FAIL these provider calls pass the goal's bare token — a hang there is unbounded:"
    echo "$BARE"
    exit 1
fi
echo "gate 16 (deadline wiring): PASS"

echo "M6 gate: PASS"
