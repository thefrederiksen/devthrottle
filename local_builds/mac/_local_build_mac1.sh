#!/usr/bin/env bash
# macOS counterpart of local_builds/_local_build_avalonia1.bat
# Builds CC Director (Avalonia) into local_builds/mac/cc-director-mac1
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$SCRIPT_DIR/../../scripts/local-build-mac.sh" --slot 1 "$@"
echo ""
echo "Exe location: $SCRIPT_DIR/cc-director-mac1"
