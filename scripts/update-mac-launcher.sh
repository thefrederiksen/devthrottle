#!/usr/bin/env bash
#
# update-mac-launcher.sh — Bring a Mac's cc-launcher up to a release, and make it actually take effect.
#
# WHY THIS EXISTS
#   On macOS the launcher never updates itself. Its updater asks for the Windows asset by name and
#   stages to a path ending in ".exe", and its only caller is gated to Windows, so the Mac launcher
#   asset is built and published in every release and nothing but the installer consumes it. A Mac
#   therefore keeps whatever launcher its last installer run left behind, for ever, while the Director
#   updates itself every week. On the owner's machine that gap reached five releases: Director 2.0.7
#   against launcher 1.9.10.
#
#   That matters more than a version number, because the launcher is what installs the DIRECTOR's
#   update. A fix to the update path ships inside the launcher, so a Mac running an old launcher keeps
#   the old behaviour no matter how many Director releases are cut.
#
#   The setup command line does the download and the swap correctly. It does NOT restart the launcher,
#   so the new binary sits on disk while the old process keeps running - the update looks done and has
#   not happened. This script closes that gap and proves the result.
#
# WHAT IT DOES
#   1. Downloads the setup command line for this release (or reuses one you pass in)
#   2. Backs up the installed launcher binary
#   3. Updates ONLY the cc-launcher component
#   4. Restarts the launch agent so the new build is the running one
#   5. Verifies the running launcher reports the expected version, and restores the backup if it does not
#
# DELIBERATELY NOT DONE
#   The Director is left alone. The setup command line reports the Director as "not installed" on macOS
#   because it looks under the instance root while the application lives in ~/Applications, so a
#   whole-role update would lay down a SECOND Director rather than update the real one. Until that is
#   fixed, this script stays scoped to the one component it can update correctly.
#
# USAGE
#   scripts/update-mac-launcher.sh                  # latest release
#   scripts/update-mac-launcher.sh v2.0.8           # a specific release
#   SETUP_CLI=/path/to/cli scripts/update-mac-launcher.sh   # reuse a downloaded command line

set -euo pipefail

TAG="${1:-latest}"
REPO="${DEVTHROTTLE_REPO:-thefrederiksen/devthrottle}"
ROOT="$HOME/Library/Application Support/cc-director"
LAUNCHER="$ROOT/instances/default/launcher/cc-launcher"
REGISTRATION="$ROOT/config/launcher/launcher.json"
LABEL="com.devthrottle.cc-launcher"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/devthrottle-launcher-update.XXXXXX")"

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }
die()  { printf '\n\033[31mFAILED: %s\033[0m\n' "$*" >&2; exit 1; }

[ "$(uname -s)" = "Darwin" ] || die "this script is for macOS only."

# ---------------------------------------------------------------------------
say "1. Reading what is installed now"
# ---------------------------------------------------------------------------
[ -f "$LAUNCHER" ] || die "no launcher at $LAUNCHER. Run the full installer first."

# The version of the RUNNING launcher, from the registration it writes about itself. Read rather than
# executed: running the launcher binary to ask its version starts a second launcher, which re-registers
# its own autostart and rewrites the launch agent underneath the one that is running.
version_running() {
    [ -f "$REGISTRATION" ] || { echo "unknown"; return; }
    /usr/bin/python3 - "$REGISTRATION" <<'PY' 2>/dev/null || echo "unknown"
import json, sys
try:
    with open(sys.argv[1]) as f:
        print((json.load(f).get("version") or "unknown").split("+")[0])
except Exception:
    print("unknown")
PY
}

BEFORE="$(version_running)"
OLD_PID="$(pgrep -f "cc-launcher --managed" | head -1 || true)"
info "launcher on disk    : $LAUNCHER"
info "version running     : $BEFORE"
info "launcher process    : ${OLD_PID:-none running}"

# ---------------------------------------------------------------------------
say "2. Backing the current launcher up"
# ---------------------------------------------------------------------------
BACKUP="$WORK/cc-launcher.$BEFORE.backup"
cp -p "$LAUNCHER" "$BACKUP"
info "backup: $BACKUP"

restore_backup() {
    printf '\n\033[33mRestoring the previous launcher.\033[0m\n'
    cp -p "$BACKUP" "$LAUNCHER" || true
    chmod +x "$LAUNCHER" || true
    /bin/launchctl kickstart -k "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || true
}

# ---------------------------------------------------------------------------
say "3. Getting the setup command line"
# ---------------------------------------------------------------------------
if [ -n "${SETUP_CLI:-}" ]; then
    CLI="$SETUP_CLI"
    info "using the one you passed: $CLI"
else
    CLI="$WORK/devthrottle-setup-cli-mac-arm64"
    command -v gh >/dev/null 2>&1 || die "the GitHub command line (gh) is needed to download a release, or pass SETUP_CLI=<path>."
    if [ "$TAG" = "latest" ]; then
        gh release download --repo "$REPO" --pattern "devthrottle-setup-cli-mac-arm64" --dir "$WORK" >/dev/null
    else
        gh release download "$TAG" --repo "$REPO" --pattern "devthrottle-setup-cli-mac-arm64" --dir "$WORK" >/dev/null
    fi
    info "downloaded from release: $TAG"
fi
chmod +x "$CLI"
# Ad-hoc signed, never notarized (no Apple developer account), so Gatekeeper would refuse a quarantined
# copy. Stripped BEFORE first launch, which is the only point at which it can be stripped.
xattr -dr com.apple.quarantine "$CLI" 2>/dev/null || true

# ---------------------------------------------------------------------------
say "4. Updating the launcher"
# ---------------------------------------------------------------------------
# The version the installer records for a component, which is the thing the running launcher must end
# up matching. Read from the machine-readable output so a wording change cannot break the check.
version_installed() {
    "$CLI" status --json 2>/dev/null | /usr/bin/python3 -c '
import json, sys
try:
    for c in json.load(sys.stdin):
        if c.get("id") == "cc-launcher":
            print(c.get("version") or "unknown"); break
    else:
        print("unknown")
except Exception:
    print("unknown")
' || echo "unknown"
}

"$CLI" plan --dry-run || true
"$CLI" update --component cc-launcher --log-file "$WORK/update.log" || {
    restore_backup
    die "the update itself failed; see $WORK/update.log"
}

TARGET="$(version_installed)"
# Written as "if", not "test && {...}": under set -e a false test at the end of a list exits the script,
# so the short form would abort every successful run.
if [ "$TARGET" = "unknown" ]; then
    restore_backup
    die "could not read back which launcher version is installed."
fi
info "installed on disk   : $TARGET"

if [ "$TARGET" = "$BEFORE" ] && [ -n "${OLD_PID:-}" ]; then
    say "Already current. The running launcher is $BEFORE and nothing needed restarting."
    exit 0
fi

# ---------------------------------------------------------------------------
say "5. Restarting the launch agent so the new build is the running one"
# ---------------------------------------------------------------------------
# Without this the swap is invisible: the replaced file sits on disk and the old process keeps running
# until the next login. KeepAlive is SuccessfulExit=false, so a clean stop is NOT respawned - kickstart
# is what both stops and starts it.
/bin/launchctl kickstart -k "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || info "kickstart reported a problem; checking anyway"

# launchd throttles respawns to roughly ten seconds, so a check straight after the kill finds nothing
# and means nothing. Wait for the process, then wait for it to write its registration.
info "waiting for the launcher to come back..."
NEW_PID=""
for _ in $(seq 1 40); do
    NEW_PID="$(pgrep -f "cc-launcher --managed" | head -1 || true)"
    [ -n "$NEW_PID" ] && [ "$NEW_PID" != "${OLD_PID:-}" ] && break
    NEW_PID=""
    sleep 1
done
[ -n "$NEW_PID" ] || { restore_backup; die "the launcher did not come back. The previous build has been restored."; }

for _ in $(seq 1 30); do
    [ "$(version_running)" = "$TARGET" ] && break
    sleep 1
done

# ---------------------------------------------------------------------------
say "6. Verifying"
# ---------------------------------------------------------------------------
AFTER="$(version_running)"
if [ "$AFTER" != "$TARGET" ]; then
    restore_backup
    die "the launcher came up reporting '$AFTER' but '$TARGET' was installed. The previous build has been restored."
fi

info "version running     : $BEFORE -> $AFTER"
info "launcher process    : $NEW_PID"
info "backup kept at      : $BACKUP"

# The launcher records whether it could register its own autostart. On 2.0.7 this fails on macOS, so
# say so rather than leaving a machine that silently will not start the launcher at the next login.
if /usr/bin/grep -q '"autostartOk":false' "$REGISTRATION" 2>/dev/null; then
    printf '\n\033[33mNOTE: this launcher could not register its own autostart.\033[0m\n'
    printf '  Existing machines are unaffected while ~/Library/LaunchAgents/%s.plist is present.\n' "$LABEL"
    printf '  Check it with: plutil -lint ~/Library/LaunchAgents/%s.plist\n' "$LABEL"
    if [ ! -f "$HOME/Library/LaunchAgents/$LABEL.plist" ]; then
        printf '\033[31m  That file is MISSING - this launcher will not start at the next login.\033[0m\n'
    fi
fi

say "Done. The launcher is $AFTER and running."
