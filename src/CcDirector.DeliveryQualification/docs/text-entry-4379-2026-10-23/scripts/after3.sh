#!/bin/bash
# Issue #4379 after-run on main with #4380 and the harness cells (#4395) merged, against the baseline.
# Expected cost: Claude Code and Codex on subscriptions; Pi on terra about 25 cents (stop at 50).
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
B=/home/soren/ReposFred/devthrottle.worktrees/tp-4379/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/tp4379-after3
echo "== echo $(date -u)"; $B --echo --routes all --texts all --out $R/echo > $R/echo.log 2>&1; echo "echo exit $?"; cat $R/echo/echo-summary.txt
echo "== text $(date -u)"; $B --text-matrix --agents claude,codex,pi --repeat 1 --max-turns 240 --out $R/text > $R/text.log 2>&1; echo "text exit $?"; cat $R/text/agent-summary.txt
echo "== conditions $(date -u)"; $B --condition-matrix --agents claude,codex,pi --out $R/conditions > $R/conditions.log 2>&1; echo "conditions exit $?"; cat $R/conditions/*summary*.txt
echo "== done $(date -u)"
