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

# -E makes the ERR trap below fire inside functions too.
set -Eeuo pipefail

REPO="${DEVTHROTTLE_REPO:-thefrederiksen/devthrottle}"
ASSET="devthrottle-setup-mac-arm64.zip"
MANIFEST="release-manifest.json"
APP_NAME="DevThrottle Setup.app"
DESTINATION_DIR="$HOME/Applications"
BASE_URL="https://github.com/$REPO/releases/latest/download"

GATEWAY_URL="${DEVTHROTTLE_HOSTED_GATEWAY_URL:-https://gateway.devthrottle.com}"

# Every line this script prints also goes to a log file the user (and support) can find, next to the
# setup wizard's own logs. Writing it is best effort: a log that cannot be written must not stop the install.
LOG_DIR="$HOME/Library/Application Support/cc-director/logs/setup"
LOG_FILE="$LOG_DIR/install-mac-$(date +%Y%m%d-%H%M%S).log"
mkdir -p "$LOG_DIR" 2>/dev/null || LOG_FILE=""

# The step the script is on, carried by every failure report. It used to say "download" whatever failed.
STEP="preconditions"

log() {
    printf '%s\n' "$*"
    if [[ -n "$LOG_FILE" ]]; then printf '%s %s\n' "$(date +%H:%M:%S)" "$*" >> "$LOG_FILE" 2>/dev/null || true; fi
}

# Send a failed step to DevThrottle (issue #3311), so the failure is not only on this screen. No sign-in
# exists yet, and none is needed. It sends the error text (home folder reduced to "~"), the macOS version,
# the architecture, and the same per-machine install id the setup wizard uses. The install has already
# failed when this runs, so a report that cannot be delivered changes nothing: the error above stands.
report_failure() {
    local message="$1" id_dir="$HOME/Library/Application Support/cc-director" id=""
    if [[ -s "$id_dir/install-id" ]]; then
        id="$(cat "$id_dir/install-id")"
    else
        id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
        { mkdir -p "$id_dir" && printf '%s' "$id" > "$id_dir/install-id"; } 2>/dev/null || true
    fi
    # The replacement is a variable because a bare ~ there is tilde-expanded straight back into $HOME.
    local tilde='~'
    message="${message//"$HOME"/$tilde}"
    message="${message//$'\n'/ }"
    message="${message//$'\t'/ }"
    message="${message//\\/\\\\}"
    message="${message//\"/\\\"}"
    local body
    body="{\"install_id\":\"$id\",\"installer\":\"install-mac.sh\",\"component\":\"setup-wizard\",\"step\":\"$STEP\",\"message\":\"$message\",\"os\":\"macos\",\"os_version\":\"$(sw_vers -productVersion 2>/dev/null || true)\",\"arch\":\"$(uname -m)\",\"product_version\":\"latest\"}"
    if curl -fsS -m 8 -H 'Content-Type: application/json' -d "$body" "$GATEWAY_URL/install-reports" >/dev/null 2>&1; then
        printf 'A report of this failure was sent to DevThrottle.\n' >&2
    fi
}

fail() {
    # The install has already failed: nothing below may stop the script before it has said so.
    trap - ERR
    set +e
    printf 'ERROR: %s\n' "$*" >&2
    if [[ -n "$LOG_FILE" ]]; then
        printf '%s ERROR (step %s): %s\n' "$(date +%H:%M:%S)" "$STEP" "$*" >> "$LOG_FILE" 2>/dev/null || true
    fi
    report_failure "$*"
    if [[ -n "$LOG_FILE" ]]; then printf 'The log of this run is in %s\n' "$LOG_FILE" >&2; fi
    exit 1
}

# A command that fails without its own "|| fail" (mkdir, mv, open, shasum...) stops the script through
# set -e. Without this trap that stop was silent to us: no report, and no ERROR line of our own.
trap 'fail "Unexpected failure at line $LINENO while running: $BASH_COMMAND (exit $?)"' ERR

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
STEP="download"
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
STEP="verify"
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
STEP="unpack"
log "Unpacking..."
ditto -xk "$WORK_DIR/$ASSET" "$WORK_DIR/unpacked" || fail "Could not unpack $ASSET."
[[ -d "$WORK_DIR/unpacked/$APP_NAME" ]] || fail "The archive did not contain \"$APP_NAME\"."

# curl downloads carry no quarantine flag, but clear any that may have been
# inherited so Gatekeeper has nothing to evaluate.
xattr -dr com.apple.quarantine "$WORK_DIR/unpacked/$APP_NAME" 2>/dev/null || true

# ----------------------------------------------------------------------------
# Install to ~/Applications, replacing any previous copy.
# ----------------------------------------------------------------------------
STEP="place"
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
STEP="open"
if [[ "${DEVTHROTTLE_NO_OPEN:-}" == "1" ]]; then
    log "DEVTHROTTLE_NO_OPEN=1 - not opening the wizard. Open it later with:"
    log "  open \"$DESTINATION_DIR/$APP_NAME\""
else
    log "Opening the setup wizard..."
    open "$DESTINATION_DIR/$APP_NAME"
fi
