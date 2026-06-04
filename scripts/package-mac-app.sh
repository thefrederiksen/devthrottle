#!/usr/bin/env bash
#
# package-mac-app.sh — Wrap a self-contained CC Director binary in a *distributable*
# macOS .app bundle, ad-hoc code-sign it, and zip it for a GitHub Release.
#
# Unlike local_builds/mac/make-app-bundle.sh (which bakes in absolute paths to a
# dev's binary and ~/.dotnet for local use), this produces a RELOCATABLE bundle:
# the self-contained binary lives INSIDE Contents/MacOS, so the .app can be moved
# to any machine with no .NET runtime installed.
#
# Distribution model (no Apple Developer account, zero cost):
#   - Ad-hoc code signature (`codesign --sign -`) so the binary has a stable
#     identity but is not Developer-ID / notarized.
#   - The auto-updater strips the Gatekeeper quarantine flag on install, so after
#     the first launch there are no warnings. The very first download still needs
#     a one-time right-click -> Open (documented in local_builds/mac/README.md).
#
# Usage:
#   scripts/package-mac-app.sh --binary <path-to-self-contained-binary> --out <output-dir>
#
# Produces:  <output-dir>/cc-director-mac-arm64.zip  (contains "CC Director.app")
#
set -euo pipefail

APP_NAME="CC Director"
BIN_NAME="cc-director"                 # CFBundle binary name (matches AssemblyName)
BID="com.centerconsulting.ccdirector"
ZIP_NAME="cc-director-mac-arm64.zip"

BINARY=""
OUT_DIR=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --binary) BINARY="$2"; shift 2 ;;
        --out)    OUT_DIR="$2"; shift 2 ;;
        --app-name) APP_NAME="$2"; shift 2 ;;   # e.g. "CC Director Setup"
        --bin-name) BIN_NAME="$2"; shift 2 ;;    # the binary inside the bundle (matches AssemblyName)
        --zip-name) ZIP_NAME="$2"; shift 2 ;;    # output asset name
        --bid)      BID="$2"; shift 2 ;;          # CFBundleIdentifier (distinct per app)
        -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

[[ -n "$BINARY" ]]  || { echo "ERROR: --binary is required" >&2; exit 1; }
[[ -n "$OUT_DIR" ]] || { echo "ERROR: --out is required" >&2; exit 1; }
[[ -f "$BINARY" ]]  || { echo "ERROR: binary not found: $BINARY" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Read <Version> from the Avalonia csproj for the bundle metadata.
CSPROJ="$REPO_ROOT/src/CcDirector.Avalonia/CcDirector.Avalonia.csproj"
VERSION="$(grep -oE '<Version>[^<]+</Version>' "$CSPROJ" | head -1 | sed -E 's/<\/?Version>//g' || echo "0.0.0")"

# ----------------------------------------------------------------------------
# Build AppIcon.icns from a 256px+ source (SVG preferred) -- same approach as
# make-app-bundle.sh. Optional: a missing icon just yields the generic app icon.
# ----------------------------------------------------------------------------
_make_icns() {
    local src="$1" out="$2" tmp iset i
    tmp="$(mktemp -d)"; iset="$tmp/AppIcon.iconset"; mkdir -p "$iset"
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

# ----------------------------------------------------------------------------
# Lay out the bundle in a clean staging dir.
# ----------------------------------------------------------------------------
mkdir -p "$OUT_DIR"
STAGE="$(mktemp -d)"
APP="$STAGE/$APP_NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# The self-contained .NET binary goes inside the bundle.
cp -f "$BINARY" "$APP/Contents/MacOS/$BIN_NAME"
chmod +x "$APP/Contents/MacOS/$BIN_NAME"

# Ship appsettings.json next to the binary if the publish output had one.
SRC_SETTINGS="$(dirname "$BINARY")/appsettings.json"
[[ -f "$SRC_SETTINGS" ]] && cp -f "$SRC_SETTINGS" "$APP/Contents/MacOS/appsettings.json"

# Icon.
ICNS_OUT="$APP/Contents/Resources/AppIcon.icns"
ICON_KEY=""
PREBUILT_ICNS="$REPO_ROOT/local_builds/mac/AppIcon.icns"
SRC_SVG="$REPO_ROOT/local_builds/mac/AppIcon.svg"
SRC_ICO="$REPO_ROOT/src/CcDirector.Avalonia/app.ico"
if   [[ -f "$PREBUILT_ICNS" ]]; then cp -f "$PREBUILT_ICNS" "$ICNS_OUT"
elif [[ -f "$SRC_SVG" ]];       then _make_icns "$SRC_SVG" "$ICNS_OUT" || true
elif [[ -f "$SRC_ICO" ]];       then _make_icns "$SRC_ICO" "$ICNS_OUT" || true
fi
[[ -f "$ICNS_OUT" ]] && ICON_KEY="    <key>CFBundleIconFile</key>          <string>AppIcon</string>"

# ----------------------------------------------------------------------------
# Launcher (CFBundleExecutable). Apps launched by launchd inherit only a minimal
# PATH, so prepend the common dirs where `claude`/homebrew tools live, then exec
# the self-contained binary by RELATIVE path. Self-contained => no DOTNET_ROOT.
# ----------------------------------------------------------------------------
cat > "$APP/Contents/MacOS/launch" <<'LAUNCH'
#!/bin/bash
# Auto-generated launcher for CC Director. Do not edit by hand.
DIR="$(cd "$(dirname "$0")" && pwd)"
for d in "$HOME/.local/bin" "$HOME/.claude/local" "/opt/homebrew/bin" \
         "/opt/homebrew/sbin" "/usr/local/bin"; do
    [[ -d "$d" ]] && case ":$PATH:" in *":$d:"*) ;; *) PATH="$d:$PATH" ;; esac
done
export PATH
cd "$DIR"
exec "$DIR/@BIN@" "$@"
LAUNCH
# Substitute the real binary name (the heredoc is quoted so runtime $VARS stay literal).
sed -i.bak "s#@BIN@#$BIN_NAME#" "$APP/Contents/MacOS/launch" && rm -f "$APP/Contents/MacOS/launch.bak"
chmod +x "$APP/Contents/MacOS/launch"

# ----------------------------------------------------------------------------
# Info.plist. Native arm64 only (matches the build RID); telling LaunchServices
# the bundle is native avoids the spurious "install Rosetta" prompt.
# ----------------------------------------------------------------------------
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
    <key>LSArchitecturePriority</key>  <array><string>arm64</string></array>
</dict>
</plist>
PLIST

# ----------------------------------------------------------------------------
# Ad-hoc code signature (free; no Apple Developer account). Sign nested code
# first via --deep, force to overwrite the publish-time signature on the binary.
# ----------------------------------------------------------------------------
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP" 2>&1 | sed 's/^/  codesign: /' || true

# ----------------------------------------------------------------------------
# Zip with ditto so the bundle's symlinks/perms/resource forks survive transit.
# ----------------------------------------------------------------------------
ZIP_PATH="$OUT_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP_PATH"
rm -rf "$STAGE"

echo "Packaged $APP_NAME v$VERSION -> $ZIP_PATH"
