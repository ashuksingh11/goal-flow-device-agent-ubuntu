#!/usr/bin/env bash
# v9 gate — A RATE LIMIT IS NOT A DROPPED SOCKET.
#
# Chains v8-M1 (→ v7-M7 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# WHY IT EXISTS. Two planner_notice lines in the user's transcript, one after the other,
# both carrying the raw text "Status: 429 (Too Many Requests)", both inside about a second.
# Every retry site in GoalAgent waited 400ms x attempt — right for a stream that hiccupped,
# two orders of magnitude short for a quota window measured in seconds. All three attempts
# were spent before the window could possibly have reopened, so the retries were not
# retries; they were the same doomed request sent three times, with three notices to show
# for it.
#
# Gate 30 asserts on the DELAY, using the exact exception text from that report, and on the
# half that is about NOT changing: a non-429 transient error still retries in 400ms, because
# making every transient error wait two seconds would be a latency regression wearing the
# mask of a fix. It also pins the cool-off as a FLOOR — a later, shorter 429 must not pull
# the wait back in and let the next call fire into a window we already knew was closed.
#
# No API key, no network: the classifier and the delay are pure functions, and the fixture
# is the provider's own error text.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v8-m1/check.sh "$@"

dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-backoff

echo "v9 gate: PASS"
