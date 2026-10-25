#!/bin/bash
# Issue #4379: the widened edge-trim rule, proven. Echo layer for edge-spaces (free), then the text matrix for edge-spaces
# with that echo run as the proof. Expected cost: 9 one-word turns; Pi under 1 cent.
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
B=/home/soren/ReposFred/devthrottle.worktrees/tp-4379/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/tp4379-trim
$B --echo --routes all --texts edge-spaces --out $R/echo > $R/echo.log 2>&1; cat $R/echo/echo-summary.txt
$B --text-matrix --agents claude,codex,pi --texts edge-spaces --repeat 1 --echo-proof $R/echo/echo-results.jsonl --out $R/text > $R/text.log 2>&1; grep "^\[agents\] \(claude\|codex\|pi\) " $R/text.log; cat $R/text/agent-summary.txt
echo "== done $(date -u)"
