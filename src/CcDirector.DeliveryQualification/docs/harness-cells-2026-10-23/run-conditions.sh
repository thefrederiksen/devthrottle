#!/bin/bash
# The condition cells again, after the first try found two faults in the rig's own readers (commit 77e8f016a).
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
W=/home/soren/ReposFred/devthrottle.worktrees/tp-harness-cells
B=$W/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/harness-cells-20261023-1309
echo "== conditions $(date -u)"; $B --condition-matrix --agents claude,codex,pi --out $R/conditions > $R/conditions.log 2>&1; echo "conditions exit $?"
echo "== done $(date -u)"
