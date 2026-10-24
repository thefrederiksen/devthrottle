#!/usr/bin/env bash
set -uo pipefail
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
ROOT="/home/soren/ReposFred/devthrottle.worktrees/tp-4385"
B="$ROOT/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification"
R="$ROOT/src/CcDirector.DeliveryQualification/docs/conformance-4385-2026-10-24/current-run"
mkdir -p "$R"
printf '%s\n' 'Expected cost before run: Claude Code uses the existing subscription. Pi makes about five small turns on mindzie dev Azure gpt-5.6-terra with thinking off; expected billed cost is cents (under $0.10). Codex is pending until its allowance resets.' > "$R/expected-cost.txt"
"$B" --conformance --agents claude,pi --args-pi '--model azure-openai-responses/gpt-5.6-terra --thinking off' --out "$R" > "$R/conformance.log" 2>&1
code=$?
printf 'exit code: %s\n' "$code" | tee "$R/exit.txt"
exit "$code"
