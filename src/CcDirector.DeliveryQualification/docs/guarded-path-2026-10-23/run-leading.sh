#!/bin/bash
# Issue #4380: the leading "!" and "/" texts on the prompt route again, after Codex's clear keys became End then Ctrl+U.
# Cost: Pi four sends (a few cents at most); Claude Code and Codex on their subscriptions.
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_USE_POLLING_FILE_WATCHER=1
W=/home/soren/ReposFred/devthrottle.worktrees/tp-4380-proof
B=$W/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
R=/home/soren/ReposFred/_delivery-qa/runs/guarded-path-20261023
echo "== leading $(date -u)"; $B --text-matrix --agents claude,codex,pi --routes prompt --texts lead-slash,lead-bang --out $R/leading 2>&1 | tee $R/leading.log; echo "leading exit ${PIPESTATUS[0]}"
echo "== done $(date -u)"
