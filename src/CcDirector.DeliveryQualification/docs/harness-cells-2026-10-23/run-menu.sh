#!/bin/bash
# The menu cells again, after the first run read the menu with the composer reader, which failed on a fresh session.
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
W=/home/soren/ReposFred/devthrottle.worktrees/tp-harness-cells
B=$W/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/harness-cells-20261023-1309
echo "== menu $(date -u)"; $B --condition-matrix --conditions menu-open --agents claude --out $R/menu > $R/menu.log 2>&1; echo "menu exit $?"
echo "== done $(date -u)"
