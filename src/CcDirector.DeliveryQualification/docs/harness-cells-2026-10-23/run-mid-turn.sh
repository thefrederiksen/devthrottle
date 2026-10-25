#!/bin/bash
# The mid-turn cells again, after the review of pull request 4395 (finding 1): the tool now proves it was running.
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
W=/home/soren/ReposFred/devthrottle.worktrees/tp-harness-cells
B=$W/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/harness-cells-20261023-1309
echo "== mid-turn $(date -u)"; $B --condition-matrix --conditions mid-turn --agents claude,codex,pi --out $R/mid-turn > $R/mid-turn.log 2>&1; echo "mid-turn exit $?"
echo "== done $(date -u)"
