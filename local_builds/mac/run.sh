#!/usr/bin/env bash
# Launches the built cc-director from a terminal with the correct .NET runtime
# environment. The framework-dependent build needs DOTNET_ROOT pointed at the
# SDK installed under ~/.dotnet (Finder/your shell don't set this by default).
#
# Usage:  ./local_builds/mac/run.sh [--slot N] [-- <app args>]
set -euo pipefail

SLOT="1"
if [[ "${1:-}" == "--slot" ]]; then SLOT="$2"; shift 2; fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$SCRIPT_DIR/cc-director-mac${SLOT}"

if [[ ! -x "$BIN" ]]; then
    echo "ERROR: $BIN not found. Build it first: ./local_builds/mac/_local_build_mac${SLOT}.sh" >&2
    exit 1
fi

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
cd "$SCRIPT_DIR"
exec "$BIN" "$@"
