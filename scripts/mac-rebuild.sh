#!/usr/bin/env bash
#
# mac-rebuild.sh — Build a CC Director target and refresh its macOS app.
#
# This is the one command you run while developing. It builds the requested
# binary (via local-build-mac.sh), then (re)creates the matching .app bundle in
# /Applications. For the main target it also pins the app to the Dock.
#
# Targets:
#   main        Build the stable copy -> "CC Director.app", pinned to the Dock.
#   1|2|3|4     Build a dev test slot -> "CC Director N.app" (run several at once).
#   all         Build main + all four slots.
#   apps        (Re)create all five .app bundles WITHOUT building — fast, used by
#               mac-setup.sh to lay down the icons before anything is built.
#
# Usage:
#   scripts/mac-rebuild.sh main
#   scripts/mac-rebuild.sh 2
#   scripts/mac-rebuild.sh all
#
# Env:
#   APPS_DIR    Where to install the apps (default /Applications).
#
set -euo pipefail

TARGET="${1:-}"
if [[ -z "$TARGET" ]]; then
    echo "Usage: scripts/mac-rebuild.sh <main|1|2|3|4|all|apps>" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MAC_DIR="$REPO_ROOT/local_builds/mac"
APPS_DIR="${APPS_DIR:-/Applications}"
export APPS_DIR

# Build one target's binary, then (re)make its app bundle.
build_one() {
    local t="$1" slot
    if [[ "$t" == "main" ]]; then slot="-main"; else slot="$t"; fi
    echo "==> Building $t ..."
    "$REPO_ROOT/scripts/local-build-mac.sh" --slot "$slot"
    "$MAC_DIR/make-app-bundle.sh" --target "$t"
}

# Pin the main app to the Dock. Always unpins any existing 'CC Director' tile
# first, rebuilds the icon cache, then re-pins — so the tile reliably shows the
# current icon even if an earlier (icon-less) bundle was cached by the Dock.
pin_dock() {
    local app="$APPS_DIR/CC Director.app"
    touch "$app"
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
        -f "$app" 2>/dev/null || true

    # Remove any existing 'CC Director' (main, not the numbered slots) tile.
    python3 - <<PY 2>/dev/null || true
import subprocess, plistlib
data = subprocess.run(["defaults","export","com.apple.dock","-"],capture_output=True).stdout
pl = plistlib.loads(data)
def is_main(e):
    try: s = e["tile-data"]["file-data"]["_CFURLString"].replace("%20"," ")
    except Exception: return False
    return s.rstrip("/").endswith("/CC Director.app")
pl["persistent-apps"] = [e for e in pl.get("persistent-apps",[]) if not is_main(e)]
subprocess.run(["defaults","import","com.apple.dock","-"],input=plistlib.dumps(pl))
PY

    # Force the icon-services cache to rebuild, then re-pin fresh.
    killall iconservicesagent 2>/dev/null || true
    defaults write com.apple.dock persistent-apps -array-add \
        "<dict><key>tile-data</key><dict><key>file-data</key><dict><key>_CFURLString</key><string>file://$app/</string><key>_CFURLStringType</key><integer>15</integer></dict></dict></dict>"
    killall Dock
    echo "Dock: pinned 'CC Director' (the Dock restarted briefly — that's normal)."
}

case "$TARGET" in
    main)
        build_one main
        pin_dock ;;
    1|2|3|4)
        build_one "$TARGET"
        echo "Open it from Launchpad/Spotlight, or: open \"$APPS_DIR/CC Director $TARGET.app\"" ;;
    all)
        build_one main
        for n in 1 2 3 4; do build_one "$n"; done
        pin_dock ;;
    apps)
        for t in main 1 2 3 4; do "$MAC_DIR/make-app-bundle.sh" --target "$t"; done ;;
    *)
        echo "ERROR: invalid target '$TARGET' (use: main|1|2|3|4|all|apps)" >&2
        exit 1 ;;
esac
