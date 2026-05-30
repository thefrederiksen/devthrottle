#!/usr/bin/env bash
#
# make-app-bundle.sh — Wraps the built cc-director binary in a double-clickable
# macOS .app bundle.
#
# Why a bundle: a bare Mach-O binary isn't launchable from Finder/Dock, and —
# more importantly — launching from a .app runs cc-director under launchd,
# completely outside any Claude Code session. That gives the `claude` processes
# it spawns clean, interactive stdio (the macOS analog of the Windows Task
# Scheduler / svchost trick described in CLAUDE.md section 0b). Run it this way
# and the Terminal/Wingman tabs work instead of dying with the "--print" error.
#
# Usage:
#   local_builds/mac/make-app-bundle.sh [--slot N]
#
# Produces: local_builds/mac/CC Director.app
#
set -euo pipefail

SLOT="1"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --slot) SLOT="$2"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="$SCRIPT_DIR"
BIN_PATH="$BIN_DIR/cc-director-mac${SLOT}"

if [[ ! -x "$BIN_PATH" ]]; then
    echo "ERROR: built binary not found at $BIN_PATH" >&2
    echo "Build it first:  ./local_builds/mac/_local_build_mac${SLOT}.sh" >&2
    exit 1
fi

# Read version from the Avalonia csproj for the bundle metadata.
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSPROJ="$REPO_ROOT/src/CcDirector.Avalonia/CcDirector.Avalonia.csproj"
VERSION="$(grep -oE '<Version>[^<]+</Version>' "$CSPROJ" | head -1 | sed -E 's/<\/?Version>//g' || echo "0.0.0")"

APP="$SCRIPT_DIR/CC Director.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"

# Detect the dotnet install dir to bake into the launcher (Finder/launchd does
# not inherit your shell PATH, so a framework-dependent build needs DOTNET_ROOT).
DOTNET_DIR="$HOME/.dotnet"
if ! [[ -x "$DOTNET_DIR/dotnet" ]] && command -v dotnet >/dev/null 2>&1; then
    DOTNET_DIR="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
fi

# Launcher script. We cd into the binary's directory so the app's sidecar
# assets (ggml-metal.metal, runtimes/) resolve, then exec the real binary.
cat > "$APP/Contents/MacOS/launch" <<LAUNCH
#!/bin/bash
export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:\$PATH"
cd "$BIN_DIR"
exec "$BIN_PATH" "\$@"
LAUNCH
chmod +x "$APP/Contents/MacOS/launch"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>CC Director</string>
    <key>CFBundleDisplayName</key>     <string>CC Director</string>
    <key>CFBundleIdentifier</key>      <string>com.centerconsulting.ccdirector</string>
    <key>CFBundleExecutable</key>      <string>launch</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleVersion</key>         <string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>NSHighResolutionCapable</key> <true/>
    <key>LSMinimumSystemVersion</key>  <string>11.0</string>
</dict>
</plist>
PLIST

# Strip the quarantine flag so Gatekeeper doesn't block the unsigned bundle.
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

echo "Created: $APP"
echo "Double-click it in Finder, or run:  open \"$APP\""
