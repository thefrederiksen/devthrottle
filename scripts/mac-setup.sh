#!/usr/bin/env bash
#
# mac-setup.sh — One-time setup for CC Director on macOS.
#
# After this runs you'll have:
#   • "CC Director" — your stable copy, in /Applications AND pinned to the Dock.
#   • "CC Director 1".."CC Director 4" — dev test slots, in /Applications
#     (find them in Launchpad or Spotlight). Build a slot when you want to test.
#
# Re-running is safe (idempotent). Requires the .NET 10 SDK (see local_builds/mac/README.md).
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Remove the legacy in-repo bundle from the old flow — it shares the main
# bundle id and would confuse LaunchServices/Dock about which app to open.
rm -rf "$REPO_ROOT/local_builds/mac/CC Director.app"

echo "Setting up CC Director apps (1 main + 4 test slots)..."

# 1) Lay down all five app icons first (instant; slots have no binary yet).
"$SCRIPT_DIR/mac-rebuild.sh" apps

# 2) Build the main copy and pin it to the Dock.
"$SCRIPT_DIR/mac-rebuild.sh" main

cat <<'EOF'

✅ Done.

  • "CC Director" is now in your Dock (bottom toolbar) and in /Applications.
    Click the Dock icon any time — this is your everyday copy.

  • Test slots "CC Director 1" … "CC Director 4" are in /Applications.
    Find them with Spotlight (press Cmd+Space, type "CC Director 2") or in
    Launchpad. They aren't built yet — clicking one tells you how to build it.

Everyday commands (run from the repo root):

    scripts/mac-rebuild.sh main     # rebuild your stable copy
    scripts/mac-rebuild.sh 2        # build test slot 2, then open it to test
    scripts/mac-rebuild.sh all      # rebuild everything

Each app launches under launchd, so the Terminal/Wingman tabs work correctly.
EOF
