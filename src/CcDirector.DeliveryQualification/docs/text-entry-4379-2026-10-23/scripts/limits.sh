#!/bin/bash
# Issue #4379: the echo layer (free), then the paste-size measurement on real agents with the paste limit lifted.
# Expected cost: Claude Code and Codex on subscriptions (36 one-word turns in all); Pi on terra about 5 cents.
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
B=/home/soren/ReposFred/devthrottle.worktrees/tp-4379/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/tp4379-limits
echo "== echo $(date -u)"; $B --echo --routes all --texts all --out $R/echo 2>&1 | tee $R/echo.log | tail -40
echo "== sizes idle $(date -u)"; $B --text-matrix --agents claude,codex,pi --texts size-2001,size-8k,size-50k,multiline-3k --routes prompt,desktop-paste --out $R/sizes 2>&1 | tee $R/sizes.log | grep "\[agents\]"
echo "== sizes busy $(date -u)"; $B --text-matrix --agents claude,codex,pi --texts size-2001,size-8k,size-50k,multiline-3k --routes prompt --cpu-load 32 --out $R/sizes-busy 2>&1 | tee $R/sizes-busy.log | grep "\[agents\]"
echo "== done $(date -u)"
