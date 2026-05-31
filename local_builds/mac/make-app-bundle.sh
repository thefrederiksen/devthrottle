#!/usr/bin/env bash
#
# make-app-bundle.sh — Wraps a built cc-director binary in a double-clickable
# macOS .app bundle and installs it into /Applications.
#
# Why a bundle: a bare Mach-O binary isn't launchable from Finder/Dock/Spotlight,
# and — more importantly — launching from a .app runs cc-director under launchd,
# completely outside any Claude Code session. That gives the `claude` processes
# it spawns clean, interactive stdio (the macOS analog of the Windows Task
# Scheduler / svchost trick in CLAUDE.md section 0b). Launch it any other way and
# the Terminal/Wingman tabs die with the "--print" error.
#
# Targets:
#   main         -> "CC Director.app"   <- cc-director-mac-main   (your stable copy)
#   1|2|3|4      -> "CC Director N.app" <- cc-director-macN       (dev test slots)
#
# Each target gets a distinct CFBundleIdentifier so macOS treats them as separate
# apps (own Dock tile, own Spotlight entry, can run side by side). The bundle's
# launcher references the binary by absolute path, so rebuilding the binary in
# place is picked up automatically — no need to re-make the bundle after a build.
#
# Usage:
#   local_builds/mac/make-app-bundle.sh --target main
#   local_builds/mac/make-app-bundle.sh --target 2
#   APPS_DIR=~/Applications local_builds/mac/make-app-bundle.sh --target main
#
set -euo pipefail

# Build AppIcon.icns from a 256px source PNG/ICO into $2. macOS needs an
# .iconset (specific sizes + @2x retina variants) compiled by iconutil.
_make_icns() {
    local src="$1" out="$2" tmp iset i
    tmp="$(mktemp -d)"; iset="$tmp/AppIcon.iconset"; mkdir -p "$iset"
    # Produce a 1024px base PNG. SVG is rendered with QuickLook (qlmanage), so no
    # extra tooling is required; raster sources go straight through sips.
    case "$src" in
        *.svg)
            qlmanage -t -s 1024 -o "$tmp" "$src" >/dev/null 2>&1 \
                && mv "$tmp/$(basename "$src").png" "$tmp/base.png" 2>/dev/null \
                || { rm -rf "$tmp"; return 1; } ;;
        *)
            sips -s format png "$src" --out "$tmp/base.png" >/dev/null 2>&1 \
                || { rm -rf "$tmp"; return 1; } ;;
    esac
    local sizes=(16 32 32 64 128 256 256 512 512 1024)
    local names=(icon_16x16 icon_16x16@2x icon_32x32 icon_32x32@2x \
                 icon_128x128 icon_128x128@2x icon_256x256 icon_256x256@2x \
                 icon_512x512 icon_512x512@2x)
    for i in "${!sizes[@]}"; do
        sips -z "${sizes[$i]}" "${sizes[$i]}" "$tmp/base.png" \
            --out "$iset/${names[$i]}.png" >/dev/null 2>&1 || { rm -rf "$tmp"; return 1; }
    done
    iconutil -c icns "$iset" -o "$out" || { rm -rf "$tmp"; return 1; }
    rm -rf "$tmp"
}

TARGET="main"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --target) TARGET="$2"; shift 2 ;;
        -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="$SCRIPT_DIR"                                  # where the binaries live
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
APPS_DIR="${APPS_DIR:-/Applications}"                  # override for testing

# Map target -> app display name, binary name, bundle id, and rebuild hint.
case "$TARGET" in
    main)
        APP_NAME="CC Director"
        BIN="cc-director-mac-main"
        BID="com.centerconsulting.ccdirector"
        REBUILD="scripts/mac-rebuild.sh main" ;;
    1|2|3|4)
        APP_NAME="CC Director $TARGET"
        BIN="cc-director-mac$TARGET"
        BID="com.centerconsulting.ccdirector.slot$TARGET"
        REBUILD="scripts/mac-rebuild.sh $TARGET" ;;
    *)
        echo "ERROR: invalid --target '$TARGET' (use: main|1|2|3|4)" >&2; exit 1 ;;
esac

BIN_PATH="$BIN_DIR/$BIN"

# Read version from the Avalonia csproj for the bundle metadata.
CSPROJ="$REPO_ROOT/src/CcDirector.Avalonia/CcDirector.Avalonia.csproj"
VERSION="$(grep -oE '<Version>[^<]+</Version>' "$CSPROJ" | head -1 | sed -E 's/<\/?Version>//g' || echo "0.0.0")"

# Detect the dotnet install dir to bake into the launcher (Finder/launchd do not
# inherit your shell PATH, so a framework-dependent build needs DOTNET_ROOT set).
DOTNET_DIR="$HOME/.dotnet"
if ! [[ -x "$DOTNET_DIR/dotnet" ]] && command -v dotnet >/dev/null 2>&1; then
    DOTNET_DIR="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
fi

# Apps launched by launchd inherit only a minimal PATH (/usr/bin:/bin:...), so
# CC Director can't find the CLIs it shells out to — most importantly `claude`,
# which lives in ~/.local/bin (or similar) and is NOT on that minimal PATH. Bake
# the real tool directories into the launcher's PATH. Build the list now (at
# bundle time) from dirs that exist, de-duped, so there are no empty segments.
LAUNCH_PATH="$DOTNET_DIR"
for d in "$HOME/.local/bin" "$HOME/.claude/local" "/opt/homebrew/bin" \
         "/opt/homebrew/sbin" "/usr/local/bin"; do
    case ":$LAUNCH_PATH:" in
        *":$d:"*) ;;                                  # already present
        *) [[ -d "$d" ]] && LAUNCH_PATH="$LAUNCH_PATH:$d" ;;
    esac
done

# Native CPU architecture. Telling LaunchServices the bundle is native (and
# prioritising this arch) stops the spurious "install Rosetta" prompt that a
# script-based .app otherwise triggers on Apple Silicon — macOS can't read an
# architecture from a shell-script main executable and would fall back to x86_64.
case "$(uname -m)" in
    arm64|aarch64) NATIVE_ARCH="arm64" ;;
    *)             NATIVE_ARCH="x86_64" ;;
esac

# App icon: build AppIcon.icns once from the Avalonia app.ico, then reuse it for
# every bundle. Missing icon -> bundle still works, just shows the generic icon.
ICNS="$BIN_DIR/AppIcon.icns"
SRC_SVG="$BIN_DIR/AppIcon.svg"                              # preferred (vector)
SRC_ICO="$REPO_ROOT/src/CcDirector.Avalonia/app.ico"        # fallback (raster)
if [[ ! -f "$ICNS" ]]; then
    if   [[ -f "$SRC_SVG" ]]; then _make_icns "$SRC_SVG" "$ICNS" || echo "WARN: could not build app icon from SVG; using generic" >&2
    elif [[ -f "$SRC_ICO" ]]; then _make_icns "$SRC_ICO" "$ICNS" || echo "WARN: could not build app icon; using generic" >&2
    fi
fi

APP="$APPS_DIR/$APP_NAME.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"
ICON_KEY=""
if [[ -f "$ICNS" ]]; then
    cp -f "$ICNS" "$APP/Contents/Resources/AppIcon.icns"
    ICON_KEY="    <key>CFBundleIconFile</key>          <string>AppIcon</string>"
fi

# Launcher script. Unlike a normal build, we deliberately do NOT require the
# binary to exist yet — the slot apps can be created up front and built later.
# If the binary is missing at click time, show a friendly dialog instead of
# failing silently. Otherwise cd into the binary's dir (so sidecar assets like
# runtimes/ resolve) and exec the real binary under launchd.
cat > "$APP/Contents/MacOS/launch" <<LAUNCH
#!/bin/bash
# Auto-generated launcher for "$APP_NAME". Do not edit by hand.
BIN="$BIN_PATH"
if [[ ! -x "\$BIN" ]]; then
    /usr/bin/osascript -e 'display dialog "${APP_NAME} has not been built yet. Build it from the repo with:    ${REBUILD}" buttons {"OK"} default button "OK" with icon caution with title "CC Director"'
    exit 1
fi
export DOTNET_ROOT="$DOTNET_DIR"
# launchd hands apps only a minimal PATH; prepend the dirs where claude/dotnet/
# homebrew tools live so CC Director can actually spawn sessions.
export PATH="$LAUNCH_PATH:\$PATH"
cd "$BIN_DIR"
exec "\$BIN" "\$@"
LAUNCH
chmod +x "$APP/Contents/MacOS/launch"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>     <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>      <string>$BID</string>
    <key>CFBundleExecutable</key>      <string>launch</string>
$ICON_KEY
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleVersion</key>         <string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>NSHighResolutionCapable</key> <true/>
    <key>LSMinimumSystemVersion</key>  <string>11.0</string>
    <key>LSRequiresNativeExecution</key><true/>
    <key>LSArchitecturePriority</key>  <array><string>$NATIVE_ARCH</string></array>
</dict>
</plist>
PLIST

# Strip any quarantine flag so Gatekeeper doesn't block the unsigned bundle.
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

# Nudge LaunchServices to register the (possibly new) bundle so it shows up in
# Spotlight/Launchpad promptly.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
    -f "$APP" 2>/dev/null || true

if [[ -x "$BIN_PATH" ]]; then
    echo "Created: $APP  (-> $BIN)"
else
    echo "Created: $APP  (binary not built yet -> run: $REBUILD)"
fi
