#!/bin/bash
# The proof run of the harness additions (issue #4378, the mission's 11.5), run in a visible Director terminal.
# The echo layer in full, then the NEW cells only on the real agents: the condition matrix, and the approval case,
# whose Codex ask is now read from its records.
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
W=/home/soren/ReposFred/devthrottle.worktrees/tp-harness-cells
B=$W/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/harness-cells-$(date +%Y%m%d-%H%M)
mkdir -p $R
echo "== echo $(date -u)"; $B --echo --routes all --texts all --out $R/echo > $R/echo.log 2>&1; echo "echo exit $?"
echo "== conditions $(date -u)"; $B --condition-matrix --agents claude,codex,pi --out $R/conditions > $R/conditions.log 2>&1; echo "conditions exit $?"
echo "== approval $(date -u)"; $B --state-matrix --cases approval --agents claude,codex --out $R/state > $R/state.log 2>&1; echo "approval exit $?"
echo "== done $(date -u) in $R"
