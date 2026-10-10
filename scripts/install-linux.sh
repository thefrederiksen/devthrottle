#!/usr/bin/env bash
#
# install-linux.sh - Install DevThrottle on Linux with one command:
#
#   curl -fsSL https://raw.githubusercontent.com/thefrederiksen/devthrottle/main/scripts/install-linux.sh | bash
#
# Why this script exists
# ----------------------
# This is the Linux counterpart of scripts/install-mac.sh and it has the same shape on
# purpose: download the setup wizard from the latest GitHub release with curl, verify its
# SHA-256 against release-manifest.json, place it, and hand over. The wizard installs and
# updates every other component from there.
#
# The reason for the shape differs by platform. On macOS the single command exists to get
# around Gatekeeper: a browser download is quarantined and the wizard is not notarized.
# Linux has no Gatekeeper, so the reason here is narrower and still worth it - a browser
# download of a plain ELF binary arrives without the executable bit, in ~/Downloads, with
# nothing checked against the release manifest. One command that chmods it and verifies the
# hash is a better first five minutes than a wiki page telling somebody to do that by hand.
#
# One path, deliberately. No AppImage, no .deb, no Flatpak, no PPA. Each of those is a
# separate build, a separate signing story and a separate set of bug reports, and none of
# them is needed to get a self-contained binary onto a machine.
#
# Safe to re-run: it replaces any previous copy of the wizard.

# -E makes the ERR trap below fire inside functions too.
set -Eeuo pipefail

REPO="${DEVTHROTTLE_REPO:-thefrederiksen/devthrottle}"
ASSET="devthrottle-setup-linux-x64"
MANIFEST="release-manifest.json"
BIN_NAME="devthrottle-setup"
DESTINATION_DIR="${DEVTHROTTLE_BIN_DIR:-$HOME/.local/bin}"
BASE_URL="https://github.com/$REPO/releases/latest/download"

GATEWAY_URL="${DEVTHROTTLE_HOSTED_GATEWAY_URL:-https://gateway.devthrottle.com}"
STEP="preconditions"

log()  { printf '%s\n' "$*"; }

# Send a failure to DevThrottle (issue #3645), so a Linux install that stops before the wizard runs - a
# missing library, a failed download, a checksum mismatch - is not only on this screen. No sign-in exists yet
# and none is needed: this is the public route every installer uses, with the same per-machine install
# identifier the setup wizard keeps. It sends the step, the message, the distribution and the architecture,
# with the home folder reduced to "~" and this machine's user name and host name replaced before anything
# leaves; the Gateway scrubs again on receipt. A report that cannot be delivered changes nothing: the
# install has already failed, and the reason is already on the screen.
json_string() { # text -> one quoted JSON string, scrubbed of this machine's names
    local text="$1" tilde='~' name
    # The replacement is a variable because a bare ~ there is tilde-expanded straight back into $HOME.
    if [[ -n "${HOME:-}" ]]; then text="${text//"$HOME"/$tilde}"; fi
    # A name shorter than three letters is left: as a plain substring it would take ordinary words out.
    name="$(hostname 2>/dev/null || true)"
    if [[ ${#name} -ge 3 ]]; then text="${text//"$name"/<machine>}"; fi
    name="$(id -un 2>/dev/null || true)"
    if [[ ${#name} -ge 3 ]]; then text="${text//"$name"/<user>}"; fi
    # Control characters other than tab and newline go; backslash, quote and tab are escaped; newlines are
    # joined as \n. sed and awk, not python3: a missing python3 is one of the failures this reports.
    printf '%s' "$text" | tr -d '\000-\010\013-\037' \
        | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/\t/\\t/g' \
        | awk 'BEGIN { ORS = ""; print "\"" } NR > 1 { print "\\n" } { print } END { print "\"" }'
}
report_failure() { # message
    local message="${1:0:4000}" id_dir id os_version
    id_dir="${XDG_DATA_HOME:-$HOME/.local/share}/cc-director"
    if [[ -s "$id_dir/install-id" ]]; then
        id="$(cat "$id_dir/install-id")"
    else
        id="$(cat /proc/sys/kernel/random/uuid 2>/dev/null || true)"
        [[ -n "$id" ]] || return 1
        { mkdir -p "$id_dir" && printf '%s' "$id" > "$id_dir/install-id"; } 2>/dev/null || true
    fi
    os_version="$( (. /etc/os-release 2>/dev/null && printf '%s' "${PRETTY_NAME:-}") || true)"
    local body
    body="{\"install_id\":$(json_string "$id"),\"installer\":\"install-linux.sh\",\"component\":\"setup-wizard\",\"step\":$(json_string "$STEP"),\"message\":$(json_string "$message"),\"diagnostics\":$(json_string "uid=$(id -u 2>/dev/null || true)"),\"os\":\"linux\",\"os_version\":$(json_string "$os_version"),\"arch\":$(json_string "$(uname -m)"),\"product_version\":\"latest\"}"
    command -v curl >/dev/null 2>&1 || return 1
    curl -fsS -m 8 -H 'Content-Type: application/json' -d "$body" "$GATEWAY_URL/install-reports" >/dev/null 2>&1
}
fail() {
    # The install has already failed: nothing below may stop the script before it has said so.
    trap - ERR
    set +e
    printf 'ERROR: %s\n' "$*" >&2
    if report_failure "$*"; then printf 'A report of this failure was sent to DevThrottle.\n' >&2; fi
    exit 1
}

# A command that fails without its own "|| fail" (mkdir, mv, chmod...) stops the script through set -e.
# Without this trap that stop was silent to us: no report.
trap 'fail "Unexpected failure at line $LINENO while running: $BASH_COMMAND (exit $?)"' ERR

# ----------------------------------------------------------------------------
# Preconditions: 64-bit Intel/AMD Linux.
# ----------------------------------------------------------------------------
# linux-arm64 is built by the release workflow but has never been executed by anyone, so
# this installer does not claim it. Refusing with a plain sentence is better than handing
# somebody an architecture nobody has ever run.
[[ "$(uname -s)" == "Linux" ]] || fail "This installer is for Linux only. On macOS use scripts/install-mac.sh."
[[ "$(uname -m)" == "x86_64" ]] \
    || fail "DevThrottle for Linux ships for 64-bit Intel/AMD (x86_64). This machine reports architecture '$(uname -m)'."

for cmd in curl sha256sum python3 tar; do
    command -v "$cmd" >/dev/null 2>&1 \
        || fail "'$cmd' is not installed. On Ubuntu or Debian: sudo apt-get install -y curl coreutils python3 tar"
done

# ----------------------------------------------------------------------------
# Preconditions: the shared libraries the wizard's user interface needs.
# ----------------------------------------------------------------------------
# The wizard is an Avalonia application and it links the X11, OpenGL and fontconfig stack
# even though the .NET runtime travels inside the binary. A normal Ubuntu desktop already
# has every one of these - a desktop environment pulls them in - so this check passes
# silently there. It earns its place on a server or a container image, where the wizard
# would otherwise exit with a linker error that names one library and explains nothing.
missing_libs=()
for lib in libX11.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1 libXrandr.so.2 \
           libXcursor.so.1 libXi.so.6 libXext.so.6 libGL.so.1; do
    ldconfig -p 2>/dev/null | grep -q "$lib" || missing_libs+=("$lib")
done
if [[ ${#missing_libs[@]} -gt 0 ]]; then
    log "The DevThrottle setup wizard needs some shared libraries this machine does not have:"
    for lib in "${missing_libs[@]}"; do log "  $lib"; done
    log ""
    fail "Install them first, then re-run this command. On Ubuntu or Debian:
  sudo apt-get update && sudo apt-get install -y libx11-6 libice6 libsm6 libfontconfig1 \\
      libxrandr2 libxcursor1 libxi6 libxext6 libgl1 libicu74 fonts-dejavu-core"
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

STEP="download"

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
STEP="verify"
log "Verifying the download against the release manifest..."
expected_hash="$(python3 -c "
import json
manifest = json.load(open('$WORK_DIR/$MANIFEST'))
print(manifest['assets']['$ASSET']['sha256'].lower())
")" || fail "Could not read the SHA-256 hash for $ASSET from $MANIFEST."
actual_hash="$(sha256sum "$WORK_DIR/$ASSET" | cut -d' ' -f1)"
[[ "$actual_hash" == "$expected_hash" ]] \
    || fail "SHA-256 mismatch for $ASSET: expected $expected_hash, got $actual_hash. Do not run this download - try again."
log "  SHA-256 verified: $actual_hash"

# ----------------------------------------------------------------------------
# Install to ~/.local/bin, replacing any previous copy.
# ----------------------------------------------------------------------------
STEP="place"
mkdir -p "$DESTINATION_DIR"
DESTINATION="$DESTINATION_DIR/$BIN_NAME"
if [[ -e "$DESTINATION" ]]; then
    log "Replacing the previous copy at $DESTINATION..."
    rm -f "$DESTINATION"
fi
mv "$WORK_DIR/$ASSET" "$DESTINATION"
chmod 755 "$DESTINATION"
log "Installed the setup wizard at $DESTINATION."

# ~/.local/bin is on the PATH of a normal Ubuntu login shell, but only when the directory
# already existed at login - so on the very first install it will not be, until the next
# time the user signs in. Say so rather than leaving them with a "command not found".
case ":$PATH:" in
    *":$DESTINATION_DIR:"*) ;;
    *) log "Note: $DESTINATION_DIR is not on this shell's PATH. Run the wizard by its full path, or sign out and back in." ;;
esac

# ----------------------------------------------------------------------------
# Run the wizard (skippable for unattended or scripted runs).
# ----------------------------------------------------------------------------
if [[ "${DEVTHROTTLE_NO_OPEN:-}" == "1" ]]; then
    log "DEVTHROTTLE_NO_OPEN=1 - not starting the wizard. Start it later with:"
    log "  $DESTINATION"
    exit 0
fi

# The wizard draws a window, so it needs a graphical session. Running it without one prints
# an Avalonia platform error that reads like a broken download; say what is actually wrong.
if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]]; then
    log "No graphical session was found (neither DISPLAY nor WAYLAND_DISPLAY is set), so the"
    log "wizard cannot open a window here. It is installed and ready. Run it from the desktop:"
    log "  $DESTINATION"
    exit 0
fi

log "Starting the setup wizard..."
exec "$DESTINATION"
