#!/usr/bin/env bash
#
# install-mac.sh - Install DevThrottle on macOS with one command:
#
#   curl -fsSL https://raw.githubusercontent.com/thefrederiksen/devthrottle/main/scripts/install-mac.sh | bash
#
# Why this script exists
# ----------------------
# The "DevThrottle Setup" wizard is ad-hoc code-signed, not notarized by Apple
# (notarization requires a paid Apple Developer account). Any non-notarized app
# downloaded with a browser is stamped with the com.apple.quarantine flag, and
# Gatekeeper then refuses to open it: "Apple could not verify 'DevThrottle
# Setup' is free of malware". On macOS 15 (Sequoia) and later the old
# right-click -> Open bypass is gone - the dialog only offers "Move to Trash"
# and "Done".
#
# Downloads made with curl are NOT stamped with the quarantine flag, so
# Gatekeeper never blocks them. This script is that path: it downloads the
# latest setup wizard from GitHub Releases with curl, verifies its SHA-256
# hash against the release manifest, places it in ~/Applications, and opens
# it. The wizard takes over from there.
#
# Safe to re-run: it replaces any previous copy of the wizard.

set -euo pipefail

REPO="${DEVTHROTTLE_REPO:-thefrederiksen/devthrottle}"
ASSET="devthrottle-setup-mac-arm64.zip"
MANIFEST="release-manifest.json"
APP_NAME="DevThrottle Setup.app"
DESTINATION_DIR="$HOME/Applications"
BASE_URL="https://github.com/$REPO/releases/latest/download"

log()  { printf '%s\n' "$*"; }
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# ----------------------------------------------------------------------------
# Preconditions: Apple Silicon Mac.
# ----------------------------------------------------------------------------
[[ "$(uname -s)" == "Darwin" ]] || fail "This installer is for macOS only."
[[ "$(uname -m)" == "arm64" ]] || fail "DevThrottle for macOS requires Apple Silicon. This machine reports architecture '$(uname -m)'."

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

# ----------------------------------------------------------------------------
# Download the wizard and the release manifest from the latest release.
# ----------------------------------------------------------------------------
log "Downloading the latest DevThrottle Setup wizard..."
log "  $BASE_URL/$ASSET"
curl -fL --progress-bar -o "$WORK_DIR/$ASSET" "$BASE_URL/$ASSET" \
    || fail "Could not download $ASSET. Check your internet connection and that the release exists at https://github.com/$REPO/releases/latest"
curl -fsSL -o "$WORK_DIR/$MANIFEST" "$BASE_URL/$MANIFEST" \
    || fail "Could not download $MANIFEST from the release."

# ----------------------------------------------------------------------------
# Verify the download against the SHA-256 hash recorded in the manifest.
# ----------------------------------------------------------------------------
# The manifest is parsed with JavaScript for Automation (osascript), which is
# part of every macOS install. Do NOT use /usr/bin/python3 here: it is only a
# stub that hands off to the Xcode developer tools, so on a Mac with no
# developer tools - or a broken Xcode - it fails and the install stops.
log "Verifying the download against the release manifest..."
expected_hash="$(osascript -l JavaScript -e '
ObjC.import("Foundation");
function run(argv) {
    const text = $.NSString.stringWithContentsOfFileEncodingError(argv[0], $.NSUTF8StringEncoding, null);
    return JSON.parse(ObjC.unwrap(text)).assets[argv[1]].sha256.toLowerCase();
}' "$WORK_DIR/$MANIFEST" "$ASSET")" || fail "Could not read the SHA-256 hash for $ASSET from $MANIFEST."
[[ "$expected_hash" =~ ^[0-9a-f]{64}$ ]] \
    || fail "The SHA-256 hash for $ASSET in $MANIFEST is not a 64-character hex string: '$expected_hash'."
actual_hash="$(shasum -a 256 "$WORK_DIR/$ASSET" | cut -d' ' -f1)"
[[ "$actual_hash" == "$expected_hash" ]] \
    || fail "SHA-256 mismatch for $ASSET: expected $expected_hash, got $actual_hash. Do not run this download - try again."
log "  SHA-256 verified: $actual_hash"

# ----------------------------------------------------------------------------
# Unpack with ditto so the app bundle's signature and permissions survive.
# ----------------------------------------------------------------------------
log "Unpacking..."
ditto -xk "$WORK_DIR/$ASSET" "$WORK_DIR/unpacked" || fail "Could not unpack $ASSET."
[[ -d "$WORK_DIR/unpacked/$APP_NAME" ]] || fail "The archive did not contain \"$APP_NAME\"."

# curl downloads carry no quarantine flag, but clear any that may have been
# inherited so Gatekeeper has nothing to evaluate.
xattr -dr com.apple.quarantine "$WORK_DIR/unpacked/$APP_NAME" 2>/dev/null || true

# ----------------------------------------------------------------------------
# Install to ~/Applications, replacing any previous copy.
# ----------------------------------------------------------------------------
mkdir -p "$DESTINATION_DIR"
if [[ -d "$DESTINATION_DIR/$APP_NAME" ]]; then
    log "Replacing the previous copy in $DESTINATION_DIR..."
    rm -rf "$DESTINATION_DIR/$APP_NAME"
fi
mv "$WORK_DIR/unpacked/$APP_NAME" "$DESTINATION_DIR/$APP_NAME"
log "Installed \"$APP_NAME\" into $DESTINATION_DIR."

# ----------------------------------------------------------------------------
# Open the wizard (skippable for unattended or scripted runs).
# ----------------------------------------------------------------------------
if [[ "${DEVTHROTTLE_NO_OPEN:-}" == "1" ]]; then
    log "DEVTHROTTLE_NO_OPEN=1 - not opening the wizard. Open it later with:"
    log "  open \"$DESTINATION_DIR/$APP_NAME\""
else
    log "Opening the setup wizard..."
    open "$DESTINATION_DIR/$APP_NAME"
fi
