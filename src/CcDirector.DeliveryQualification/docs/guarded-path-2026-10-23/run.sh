#!/bin/bash
# Issue #4380, the after side: the condition cells and the leading "!" and "/" texts, on main at 45e437c69
# (which holds #4380's change, efa52f2ee). The before is the harness cells' first run at 4b58b3a23 and the mission baseline.
# Expected cost, written before the run: Pi about $0.30, under $1.30 at the assumed ceiling price; stop past twice that.
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_USE_POLLING_FILE_WATCHER=1
W=/home/soren/ReposFred/devthrottle.worktrees/tp-4380-proof
B=$W/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/guarded-path-20261023
mkdir -p $R
echo "== conditions $(date -u)"; $B --condition-matrix --agents claude,codex,pi --out $R/conditions 2>&1 | tee $R/conditions.log; echo "conditions exit ${PIPESTATUS[0]}"
echo "== leading $(date -u)"; $B --text-matrix --agents claude,codex,pi --routes prompt --texts lead-slash,lead-bang --out $R/leading 2>&1 | tee $R/leading.log; echo "leading exit ${PIPESTATUS[0]}"
echo "== done $(date -u)"
