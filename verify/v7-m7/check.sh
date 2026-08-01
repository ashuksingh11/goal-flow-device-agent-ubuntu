#!/usr/bin/env bash
# v7-M7 gate — grounding does not ask the same question twice.
#
# Chains v7-M6 (→ v7-M5 → … → M0). THE LATEST MILESTONE'S check.sh IS THE ONE TO RUN.
#
# Found by a person watching a demo, not by a gate: "call to findrecipes many times…
# it is taking too long". Measured, one meal plan spent four and a half minutes making
# ten-plus calls to Recipes.FindRecipes whose arguments differed only in the ORDER of
# the tag list.
#
# The cause was a tool that could not satisfy a query and did not say so. The household
# prefers WHITE MEAT; the recipe box is entirely vegetarian and its tags read
# `more_protein`, not `high_protein`. The old filter answered by ORDERING on a preference
# count that was zero for every recipe — same five recipes, same order, no error. To the
# model that is indistinguishable from "these are your best matches", so it assumed it had
# phrased the query badly and rephrased. Ten times.
#
# Gate 28 pins both halves: FindRecipes now names the tags it could not match (and says
# not to search again), and RepeatReadFilter makes any identical read — including one
# whose list arguments are merely permuted — cost one round-trip instead of N.
set -euo pipefail
cd "$(dirname "$0")/../.."

./verify/v7-m6/check.sh "$@"

export OPENROUTER_API_KEY="${OPENROUTER_API_KEY:-dump-mode}"

dotnet run --project GoalFlow.Device.csproj --no-build -- --verify-repeat-reads

echo "v7-M7 gate: PASS"
