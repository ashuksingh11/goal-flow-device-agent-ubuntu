#!/usr/bin/env bash
# v8-M1 gate — the two OpenRouter body fields SK does not model, and the promise that they
# are INVISIBLE until someone asks for them.
#
# Chains v7-M7 (→ v7-M6 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# WHY v8 EXISTS AT ALL. Four identical standalone runs on the same afternoon took 59s, 175s,
# 145s and 189s. The input never changed; the PROVIDER did. With no `provider` field
# OpenRouter load-balances across nineteen endpoints for this model whose throughput spans
# 39x, and it kept picking the slow ones — CoreWeave at 52 tok/s, Novita at 76, against
# Cerebras at 1523. The same compose-shaped task: 50.1s unpinned, 1.5s pinned. The demo's
# latency was never a prompt problem.
#
# HALF THIS GATE IS ABOUT THE FIELDS BEING ABSENT, and that half matters more. Every other
# gate in verify/ was written against a request body with no `provider` and no
# `reasoning_effort` in it, and not one of them would notice if we started sending them —
# they never reach the network. Gate 29 does, so "unset changes nothing" is measured rather
# than assumed.
#
# It asserts on the WIRE, against a local HttpListener, because the claim under test is a
# claim about SK's serializer: that ExtraBody lands as a TOP-LEVEL `provider` key rather than
# nested under `extra_body`, and that it does so on the streaming path too — SK applies
# ExtraBody LAST in its options builder, so a colliding key would silently clobber `stream`.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v7-m7/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-request-shape

echo "v8-M1 gate: PASS"
