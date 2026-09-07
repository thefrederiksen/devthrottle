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

set -euo pipefail

REPO="${DEVTHROTTLE_REPO:-thefrederiksen/devthrottle}"
ASSET="devthrottle-setup-linux-x64"
MANIFEST="release-manifest.json"
BIN_NAME="devthrottle-setup"
DESTINATION_DIR="${DEVTHROTTLE_BIN_DIR:-$HOME/.local/bin}"
BASE_URL="https://github.com/$REPO/releases/latest/download"

log()  { printf '%s\n' "$*"; }
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

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
