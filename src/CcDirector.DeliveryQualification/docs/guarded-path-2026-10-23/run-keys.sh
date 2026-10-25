#!/bin/bash
# Issue #4380: which keys empty a Codex composer that holds a command Codex refused (cursor at the line's start).
# Cost: none billed - Codex starts and is typed into, no prompt is ever submitted to the model.
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_USE_POLLING_FILE_WATCHER=1
W=/home/soren/ReposFred/devthrottle.worktrees/tp-4380-proof
B=$W/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/guarded-path-20261023
echo "== keys $(date -u)"; $B --keys codex --keys-only ctrl-u,end-then-ctrl-u 2>&1 | tee $R/keys-codex.log; echo "keys exit ${PIPESTATUS[0]}"
echo "== done $(date -u)"
