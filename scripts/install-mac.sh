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

# Never as root. macOS keeps HOME under sudo, so everything below would be created in the user's own
# Library owned by root - and launchd then refuses to start DevThrottle with "78: EX_CONFIG" (#3411).
# Checked before anything is written, so a refused run leaves nothing behind.
if [[ "$(id -u)" -eq 0 ]]; then
    printf 'ERROR: Do not run the DevThrottle installer with sudo. It installs into your own user folder,\n' >&2
    printf 'and files created as root there stop macOS from starting DevThrottle.\n' >&2
    printf 'Run the same command again without sudo:\n\n' >&2
    printf '  curl -fsSL https://raw.githubusercontent.com/thefrederiksen/devthrottle/main/scripts/install-mac.sh | bash\n' >&2
    exit 1
fi

REPO="${DEVTHROTTLE_REPO:-thefrederiksen/devthrottle}"
ASSET="devthrottle-setup-mac-arm64.zip"
MANIFEST="release-manifest.json"
APP_NAME="DevThrottle Setup.app"
DESTINATION_DIR="$HOME/Applications"
BASE_URL="https://github.com/$REPO/releases/latest/download"

GATEWAY_URL="${DEVTHROTTLE_HOSTED_GATEWAY_URL:-https://gateway.devthrottle.com}"

# An earlier run with sudo leaves DevThrottle's files owned by root in the user's own Library. Every write
# below would then fail, and launchd refuses to start the launcher ("78: EX_CONFIG", #3411). Hand them back
# before anything is written. sudo asks for the password on the terminal even when this script is piped.
# Ours, checked and repaired all the way down.
TREE_CANDIDATES=("$HOME/Library/Application Support/cc-director" "$DESTINATION_DIR/$APP_NAME"
                 "$DESTINATION_DIR/Director.app" "$DESTINATION_DIR/CC Director.app"
                 "$HOME/Library/LaunchAgents/com.devthrottle.cc-launcher.plist"
                 "$HOME/.zshrc" "$HOME/.bash_profile")
# Shared parents a sudo run may have created: only the folder itself, never what other apps keep in it.
# Replacing anything inside a folder needs the folder to be writable, so a root-owned parent blocks on its own.
FOLDER_CANDIDATES=("$DESTINATION_DIR" "$HOME/Library/LaunchAgents" "$HOME/.local" "$HOME/.local/bin")

# Sets NOT_OWNED to the paths the user does not own. Returns nonzero when the check itself failed: find exits 1
# when it cannot descend into a root-owned folder but still lists that folder, so only a failure with nothing
# listed is a check that did not happen - and that must never read as "all owned". The filter is plain shell,
# so it has no failure of its own that could erase a path find reported. What exists is looked up on every
# check: a root-owned parent the user cannot open hides its children until the parent is repaired.
NOT_OWNED=""
find_not_owned() { # paths and expression...
    local out code=0 line found=""
    out="$(find "$@" 2>&1)" || code=$?
    while IFS= read -r line; do
        if [[ -n "$line" && "$line" != find:* ]]; then found+="$line"$'\n'; fi
    done <<<"$out"
    if [[ $code -ne 0 && -z "$found" ]]; then
        printf 'ERROR: could not check who owns the DevThrottle files: %s\n' "$out" >&2
        return 1
    fi
    NOT_OWNED+="$found"
}
check_owned() {
    local p trees=() folders=()
    for p in "${TREE_CANDIDATES[@]}"; do if [[ -e "$p" ]]; then trees+=("$p"); fi; done
    for p in "${FOLDER_CANDIDATES[@]}"; do if [[ -d "$p" ]]; then folders+=("$p"); fi; done
    NOT_OWNED=""
    # The folders themselves are read with stat: macOS find will not report a folder the user cannot open.
    for p in "${folders[@]+"${folders[@]}"}"; do
        local owner
        if ! owner="$(stat -f %u "$p" 2>&1)"; then
            printf 'ERROR: could not check who owns %s: %s\n' "$p" "$owner" >&2
            return 1
        fi
        if [[ "$owner" != "$(id -u)" ]]; then NOT_OWNED+="$p"$'\n'; fi
    done
    if [[ ${#trees[@]} -gt 0 ]]; then find_not_owned "${trees[@]}" ! -user "$(id -u)" -print || return 1; fi
}
# Parents first, then every named tree whether or not it was visible: the loop runs as root, which can see
# inside a folder the user cannot. A named path that does not exist is skipped (chown refuses one even with -f);
# a real chown failure still fails. The owner is passed as $0, expanded here, before sudo.
CHOWN_FOLDERS='for p in "$@"; do if [ -e "$p" ]; then /usr/sbin/chown "$0" "$p" || exit 1; fi; done'
CHOWN_TREES='for p in "$@"; do if [ -e "$p" ]; then /usr/sbin/chown -R "$0" "$p" || exit 1; fi; done'
repair_owned() {
    sudo /bin/sh -c "$CHOWN_FOLDERS" "$(id -u):$(id -g)" "${FOLDER_CANDIDATES[@]}" || return 1
    sudo /bin/sh -c "$CHOWN_TREES" "$(id -u):$(id -g)" "${TREE_CANDIDATES[@]}" || return 1
}
check_owned || exit 1
REPAIRED_OWNERSHIP=""
if [[ -n "$NOT_OWNED" ]]; then
    NOT_OWNED_BEFORE="$NOT_OWNED"
    printf 'Some DevThrottle files belong to another user, usually because an earlier install was run with sudo.\n'
    printf 'macOS will not start DevThrottle until they belong to you again. Enter your Mac password to repair them.\n'
    if ! repair_owned || ! check_owned || [[ -n "$NOT_OWNED" ]]; then
        printf 'ERROR: the files could not be repaired. Run this, then run the install again:\n\n' >&2
        printf '  sudo /bin/sh -c %q "$(id -u):$(id -g)"' "$CHOWN_FOLDERS" >&2; printf ' %q' "${FOLDER_CANDIDATES[@]}" >&2; printf '\n' >&2
        printf '  sudo /bin/sh -c %q "$(id -u):$(id -g)"' "$CHOWN_TREES" >&2; printf ' %q' "${TREE_CANDIDATES[@]}" >&2; printf '\n' >&2
        exit 1
    fi
    printf 'Repaired.\n'
    REPAIRED_OWNERSHIP="$NOT_OWNED_BEFORE"
fi

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

# Send one step to DevThrottle (issue #3311), so what happened is not only on this screen. No sign-in
# exists yet, and none is needed. It sends the step, the message (home folder reduced to "~"), the macOS
# version, the architecture, the same per-machine install id the setup wizard uses, and the log of this run
# so far as diagnostics - a failed install used to arrive as one line, and the five lines before it, which
# said what the script had done, stayed on the user's Mac. A report that cannot be delivered changes nothing.
json_string() { # text -> one JSON string literal, quotes included, home folder reduced to ~
    # JavaScript for Automation's JSON.stringify, which every Mac has and which the hash check above already
    # relies on: it escapes every control byte, quote and backslash correctly. A bash encoder cannot be written
    # once for both bash versions - the Mac's bash 3.2 keeps the quote characters of a quoted replacement
    # literally, bash 5.2 strips backslashes from an unquoted one - and one unescaped carriage return (curl
    # writes its progress with them) makes the whole report invalid JSON, which the Gateway refuses.
    local text="$1" tilde='~'
    # The replacement is a variable because a bare ~ there is tilde-expanded straight back into $HOME.
    text="${text//"$HOME"/$tilde}"
    osascript -l JavaScript -e 'function run(argv) { return JSON.stringify(argv[0]); }' -- "$text"
}
report_step() { # message
    local message="$1" id_dir="$HOME/Library/Application Support/cc-director" id="" run_log=""
    if [[ -s "$id_dir/install-id" ]]; then
        id="$(cat "$id_dir/install-id")"
    else
        id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
        { mkdir -p "$id_dir" && printf '%s' "$id" > "$id_dir/install-id"; } 2>/dev/null || true
    fi
    if [[ -n "$LOG_FILE" && -s "$LOG_FILE" ]]; then
        if [[ "$(wc -c < "$LOG_FILE")" -gt 12000 ]]; then
            # The cut lands on a line boundary: tail -c can split a multi-byte character, and half a
            # character is invalid JSON.
            run_log="$(tail -c 12000 "$LOG_FILE" 2>/dev/null | tail -n +2 || true)"
        else
            run_log="$(cat "$LOG_FILE" 2>/dev/null || true)"
        fi
    fi
    local diagnostics
    diagnostics="run log ($(basename "${LOG_FILE:-no log file}")):"$'\n'"${run_log:-(empty)}"$'\n'"sw_vers:"$'\n'"$(sw_vers 2>/dev/null || true)"$'\n'"id: $(id 2>/dev/null || true)"$'\n'"home: $HOME -> $(readlink "$HOME" 2>/dev/null || printf 'not a link')"
    local message_json diagnostics_json body
    message_json="$(json_string "$message")" || return 1
    diagnostics_json="$(json_string "$diagnostics")" || return 1
    body="{\"install_id\":\"$id\",\"installer\":\"install-mac.sh\",\"component\":\"setup-wizard\",\"step\":\"$STEP\",\"message\":$message_json,\"diagnostics\":$diagnostics_json,\"os\":\"macos\",\"os_version\":\"$(sw_vers -productVersion 2>/dev/null || true)\",\"arch\":\"$(uname -m)\",\"product_version\":\"latest\"}"
    curl -fsS -m 8 -H 'Content-Type: application/json' -d "$body" "$GATEWAY_URL/install-reports" >/dev/null 2>&1
}
report_failure() {
    if report_step "$1"; then
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

# The ownership check ran before the log existed; its outcome goes into the log and to DevThrottle now, so a
# repair that happened on a user's Mac is known on our side and not inferred from the next failure.
if [[ -n "$REPAIRED_OWNERSHIP" ]]; then
    STEP="repair-ownership"
    log "Repaired the ownership of: $(printf '%s' "$REPAIRED_OWNERSHIP" | tr '\n' ' ')"
    report_step "Repaired DevThrottle files that belonged to another user: $(printf '%s' "$REPAIRED_OWNERSHIP" | tr '\n' ' ')" || true
    STEP="preconditions"
fi

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

# The script's part is done. Say so to DevThrottle as well: a machine whose script finished and whose wizard
# then said nothing is a different story from one whose script never got this far, and until now both were
# silence on our side.
STEP="done"
log "install-mac.sh finished; the setup wizard takes over."
report_step "OK: install-mac.sh completed and $( [[ "${DEVTHROTTLE_NO_OPEN:-}" == "1" ]] && printf 'did not open the wizard (DEVTHROTTLE_NO_OPEN=1)' || printf 'opened the setup wizard')" || true
