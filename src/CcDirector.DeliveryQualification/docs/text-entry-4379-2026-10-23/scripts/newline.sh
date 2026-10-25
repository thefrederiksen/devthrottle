#!/bin/bash
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
B=/home/soren/ReposFred/devthrottle.worktrees/tp-4379/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
O=/home/soren/ReposFred/_delivery-qa/runs/tp4379-newline
for a in claude codex pi; do $B --newline-keys $a --out $O/$a 2>&1 | tee $O/$a.log; done
echo "== newline run done"
