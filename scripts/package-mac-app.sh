#!/usr/bin/env bash
#
# package-mac-app.sh — Wrap a self-contained CC Director binary in a *distributable*
# macOS .app bundle, ad-hoc code-sign it, and zip it for a GitHub Release.
#
# Unlike scripts/local-build/mac/make-app-bundle.sh (which bakes in absolute paths to a
# dev's binary and ~/.dotnet for local use), this produces a RELOCATABLE bundle:
# the self-contained binary lives INSIDE Contents/MacOS, so the .app can be moved
# to any machine with no .NET runtime installed.
#
# Distribution model (no Apple Developer account, zero cost):
#   - A STABLE self-signed code signature when --identity is given, and an ad-hoc
#     one when it is not. Neither is Developer-ID / notarized.
#   - The auto-updater strips the Gatekeeper quarantine flag on install, so after
#     the first launch there are no warnings. The very first download still needs
#     a one-time right-click -> Open (documented in scripts/local-build/mac/README.md).
#
# WHY --identity MATTERS, AND WHY AD-HOC IS NOT GOOD ENOUGH (issue #4518).
# macOS records a privacy permission ("DevThrottle would like to access data from other
# apps") against the app's DESIGNATED REQUIREMENT, not against its name. Ad-hoc signing has
# no certificate to name us by, so that requirement degenerates to a hash of the build:
#
#     designated => cdhash H"499b1ed8..."        <- ad-hoc: changes EVERY build
#
# The Director auto-updates on start-up, so every update produced a different hash, every
# stored grant stopped matching, and the user was asked for every permission again - forever.
# Signing with one stable certificate instead gives a requirement that survives rebuilds:
#
#     designated => identifier "com.devthrottle.ccdirector" and certificate leaf = H"65f4a7a8..."
#
# The certificate does not have to be from Apple and does not have to be trusted for this to
# work - codesign will sign with an untrusted self-signed certificate, and the requirement
# matches on the leaf hash. An Apple Developer ID certificate is still needed for Gatekeeper,
# notarization and a double-click install; this buys only the permissions, but it buys them free.
#
# Usage:
#   scripts/package-mac-app.sh --binary <path-to-self-contained-binary> --out <output-dir>
#                              [--identity <codesign identity>]
#
# Produces:  <output-dir>/cc-director-mac-arm64.zip  (contains "Director.app")
#
# --display-name gives the app the name a person SEES - in the Finder, Spotlight, Launchpad, the
# Dock and the menu bar - while the bundle keeps its folder name (issue #4427). The Director ships
# as "Director.app" with the display name "DevThrottle": every installer, updater and launcher
# path names the folder, so the folder stays, and what a person searches for is DevThrottle.
# macOS shows the localized display name only while the folder name matches the non-localized
# CFBundleDisplayName, so Info.plist keeps the folder name there and en.lproj/InfoPlist.strings
# carries the name shown.
#
set -euo pipefail

APP_NAME="Director"
DISPLAY_NAME=""                        # the name shown; defaults to APP_NAME
BIN_NAME="cc-director"                 # CFBundle binary name (matches AssemblyName)
BID="com.devthrottle.ccdirector"
ZIP_NAME="cc-director-mac-arm64.zip"

BINARY=""
OUT_DIR=""
DMG_NAME=""
# The codesign identity. Empty means ad-hoc ("-"), which is what a local build and a fork get.
# CI passes the stable self-signed identity so that privacy grants survive an update.
IDENTITY="${MACOS_SIGN_IDENTITY:-}"
# Set by CI when a signing certificate was supplied to the job. It turns "the requirement names a
# certificate" from a hope into a build failure. See the check after signing.
REQUIRE_CERTIFICATE=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --binary) BINARY="$2"; shift 2 ;;
        --out)    OUT_DIR="$2"; shift 2 ;;
        --identity) IDENTITY="$2"; shift 2 ;;   # codesign identity; empty => ad-hoc
        --require-certificate) REQUIRE_CERTIFICATE=1; shift ;;  # fail unless the result names a certificate
        --dmg-name) DMG_NAME="$2"; shift 2 ;;   # also produce a drag-to-Applications DMG
        --app-name) APP_NAME="$2"; shift 2 ;;   # e.g. "CC Director Setup"
        --display-name) DISPLAY_NAME="$2"; shift 2 ;;   # e.g. "DevThrottle" (shown; the folder keeps APP_NAME)
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
[[ -n "$DISPLAY_NAME" ]] || DISPLAY_NAME="$APP_NAME"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Read <Version> from Directory.Build.props (single source, see docs/architecture/VERSIONING.md).
PROPS="$REPO_ROOT/Directory.Build.props"
VERSION="$(grep -oE '<Version>[^<]+</Version>' "$PROPS" | head -1 | sed -E 's/<\/?Version>//g')"
[[ -n "$VERSION" ]] || { echo "ERROR: could not read <Version> from $PROPS" >&2; exit 1; }

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
SRC_SVG="$REPO_ROOT/scripts/local-build/mac/AppIcon.svg"
SRC_ICO="$REPO_ROOT/src/CcDirector.Avalonia/app.ico"
if   [[ -f "$PREBUILT_ICNS" ]]; then cp -f "$PREBUILT_ICNS" "$ICNS_OUT"
elif [[ -f "$SRC_SVG" ]];       then _make_icns "$SRC_SVG" "$ICNS_OUT" || true
elif [[ -f "$SRC_ICO" ]];       then _make_icns "$SRC_ICO" "$ICNS_OUT" || true
fi
[[ -f "$ICNS_OUT" ]] && ICON_KEY="    <key>CFBundleIconFile</key>          <string>AppIcon</string>"

# ----------------------------------------------------------------------------
# NO LAUNCHER SCRIPT. The self-contained binary IS the bundle's CFBundleExecutable.
#
# There used to be a #!/bin/bash wrapper here whose only job was to prepend the directories
# launchd leaves out of a GUI application's PATH (~/.local/bin, ~/.claude/local, Homebrew)
# before exec'ing the real binary. It cost us the app's code identity: with a script as the
# main executable, codesign reports
#
#     host => identifier "com.apple.bash" and anchor apple
#
# so macOS was being asked to grant this application's privacy permissions to Apple's copy of
# bash (issue #4518). The PATH work now happens in-process at start-up, in
# ToolShimPath.EnsureGuiLaunchPathDefaults(), which runs before any session is started - so the
# signed Mach-O binary is the main executable and carries its own identity.
#
# Self-contained => no DOTNET_ROOT, and the binary resolves its own directory, so nothing else
# the wrapper did is still needed.
# ----------------------------------------------------------------------------

# ----------------------------------------------------------------------------
# The name shown (--display-name). When it differs from the folder name, Info.plist
# says the bundle has a localized name and en.lproj carries it; English is the
# development region and Base is the fallback, so every language without its own folder shows it too.
# ----------------------------------------------------------------------------
LOCALIZED_KEYS=""
if [[ "$DISPLAY_NAME" != "$APP_NAME" ]]; then
    # English, and Base for every language the bundle has no folder for, so a Mac set to any
    # language shows the same name.
    for LPROJ in en Base; do
        mkdir -p "$APP/Contents/Resources/$LPROJ.lproj"
        cat > "$APP/Contents/Resources/$LPROJ.lproj/InfoPlist.strings" <<STRINGS
CFBundleName = "$DISPLAY_NAME";
CFBundleDisplayName = "$DISPLAY_NAME";
STRINGS
    done
    LOCALIZED_KEYS="    <key>LSHasLocalizedDisplayName</key><true/>"
fi

# ----------------------------------------------------------------------------
# Info.plist. Native arm64 only (matches the build RID); telling LaunchServices
# the bundle is native avoids the spurious "install Rosetta" prompt.
# ----------------------------------------------------------------------------
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>$DISPLAY_NAME</string>
    <key>CFBundleDisplayName</key>     <string>$APP_NAME</string>
    <key>CFBundleDevelopmentRegion</key><string>en</string>
$LOCALIZED_KEYS
    <key>CFBundleIdentifier</key>      <string>$BID</string>
    <key>CFBundleExecutable</key>      <string>$BIN_NAME</string>
$ICON_KEY
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleVersion</key>         <string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>NSMicrophoneUsageDescription</key>
    <string>DevThrottle uses the microphone for dictation: what you say is transcribed into the prompt box.</string>
    <key>NSHighResolutionCapable</key> <true/>
    <key>LSMinimumSystemVersion</key>  <string>11.0</string>
    <key>LSRequiresNativeExecution</key><true/>
    <key>LSArchitecturePriority</key>  <array><string>arm64</string></array>
</dict>
</plist>
PLIST

# ----------------------------------------------------------------------------
# Code signature. --identity (or MACOS_SIGN_IDENTITY) gives the stable self-signed certificate
# that makes privacy grants survive an update; with neither we fall back to ad-hoc, so a local
# build and a fork still produce a working bundle.
#
# Signed INSIDE-OUT rather than with --deep: Apple deprecates --deep for distribution, and it is
# the wrong tool here anyway - nested code must be signed before the bundle that seals it.
# ----------------------------------------------------------------------------
SIGN_WITH="${IDENTITY:--}"
if [[ "$SIGN_WITH" == "-" ]]; then
    echo "  codesign: AD-HOC (no --identity) - privacy grants will NOT survive an update"
else
    echo "  codesign: signing as '$SIGN_WITH'"
fi

# Inside out: any nested Mach-O before the bundle that seals it.
#
# For this bundle TODAY the first line is belt and braces - Contents/MacOS/$BIN_NAME is the bundle's
# CFBundleExecutable rather than nested code, so signing it alone gives it the identifier codesign
# derives from the FILENAME ("cc-director"), and the second line immediately replaces that with the
# CFBundleIdentifier from Info.plist. It stays because the order is the rule the moment anything else
# is added under Contents - a helper tool, a framework, a dylib - and because --deep, which used to
# paper over the order, is deprecated for distribution.
#
# The second line is the one that decides the identifier half of the designated requirement, which is
# why the check below asserts that identifier and not only the certificate.
# --timestamp=none: an untrusted self-signed certificate has no timestamp authority to ask, and
# asking would add a network call to every build for nothing. NOTARIZATION REFUSES A SIGNATURE WITH
# NO SECURE TIMESTAMP, so this flag must go when the Apple Developer ID certificate arrives - here,
# on the next line, and in the two codesign steps in .github/workflows/release.yml.
codesign --force --timestamp=none --sign "$SIGN_WITH" "$APP/Contents/MacOS/$BIN_NAME"
codesign --force --timestamp=none --sign "$SIGN_WITH" "$APP"

# NOT `| sed ... || true`. That discarded codesign's exit status, so a bundle codesign REFUSES to
# verify - a sealed resource missing or invalid, nested code left unsigned - still printed a tidy
# requirement line, passed the gates below (they read the requirement's text, not the signature's
# validity) and was zipped and published. The output is captured so it can still be indented.
VERIFY_OUTPUT="$(codesign --verify --deep --strict "$APP" 2>&1)" && VERIFY_OK=1 || VERIFY_OK=0
[[ -n "$VERIFY_OUTPUT" ]] && echo "$VERIFY_OUTPUT" | sed 's/^/  codesign: /'
if [[ "$VERIFY_OK" != "1" ]]; then
    echo "ERROR: the signature on $APP does not verify. Refusing to package." >&2
    exit 1
fi
echo "  codesign: verified (valid on disk, seal intact)"

# The designated requirement is the line that decides whether a privacy grant survives: a bare
# "cdhash H..." means it will not, an "identifier ... and certificate leaf ..." means it will.
REQUIREMENT="$(codesign -d -r- "$APP" 2>&1 | grep -E "designated|host =>" || true)"
echo "  codesign: designated requirement ->"
echo "$REQUIREMENT" | sed 's/^/    /'

# AND IT IS CHECKED, not just printed. A print fails open: the identity could arrive EMPTY because a
# secret was renamed, or scoped to an environment the job does not use, and the release would publish
# an ad-hoc bundle whose only trace is one line in a thousand-line log - breaking every user's privacy
# grants again without saying so, which is the exact bug this all exists to fix (issue #4518).
#
# Two separate checks, because "ad-hoc" is a legitimate answer for a local build and a fork and a
# defect for a release:
#   --require-certificate  CI passes it when the job HAS a certificate. An ad-hoc result is then a
#                          misconfiguration, and the empty-identity case is caught here and nowhere else.
#   an identity was given  then it must have produced a certificate-based requirement, whatever the caller
#                          believed.
if [[ "$REQUIRE_CERTIFICATE" == "1" ]] || [[ "$SIGN_WITH" != "-" ]]; then
    # BOTH HALVES OF THE REQUIREMENT ARE CHECKED. It reads
    #     identifier "<bundle id>" and certificate leaf = H"<hash>"
    # and a grant is keyed to the whole line, so the identifier matters as much as the certificate.
    # Checking only the leaf would let a future edit ship identifier "cc-director" - what codesign
    # derives from the filename when the bundle itself is not signed - and still pass.
    if ! echo "$REQUIREMENT" | grep -q "certificate leaf"; then
        echo "ERROR: the designated requirement names no certificate." >&2
        echo "       identity passed: '${IDENTITY:-(empty)}'" >&2
        echo "       A privacy grant would NOT survive an update. Refusing to package." >&2
        exit 1
    fi
    if ! echo "$REQUIREMENT" | grep -q "identifier \"$BID\""; then
        echo "ERROR: the designated requirement does not name the bundle identifier $BID:" >&2
        echo "       $REQUIREMENT" >&2
        echo "       A privacy grant would be keyed to the wrong application. Refusing to package." >&2
        exit 1
    fi
fi

# ----------------------------------------------------------------------------
# Zip with ditto so the bundle's symlinks/perms/resource forks survive transit.
# ----------------------------------------------------------------------------
ZIP_PATH="$OUT_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP_PATH"

# ----------------------------------------------------------------------------
# Optional drag-to-Applications DMG (--dmg-name). The DMG holds the .app plus
# an /Applications symlink, so a manual install is one drag and always lands in
# the canonical place - no loose unzipped app copies scattered around Downloads
# (the pile-up the installer's purge exists to clean). The zip stays the
# auto-updater's asset; the DMG is the human download.
# ----------------------------------------------------------------------------
if [[ -n "$DMG_NAME" ]]; then
    DMG_PATH="$OUT_DIR/$DMG_NAME"
    DMG_STAGE="$(mktemp -d)"
    ditto "$APP" "$DMG_STAGE/$APP_NAME.app"
    ln -s /Applications "$DMG_STAGE/Applications"
    rm -f "$DMG_PATH"
    hdiutil create -quiet -volname "$DISPLAY_NAME $VERSION" -srcfolder "$DMG_STAGE" \
        -fs HFS+ -format UDZO "$DMG_PATH"
    rm -rf "$DMG_STAGE"
    echo "Packaged $APP_NAME v$VERSION -> $DMG_PATH"
fi

rm -rf "$STAGE"

echo "Packaged $APP_NAME (shown as $DISPLAY_NAME) v$VERSION -> $ZIP_PATH"
