#!/usr/bin/env bash
#
# mac-unattended-setup.sh — Configure a Mac mini as an unattended DevThrottle fleet machine.
#
# WHY THIS SCRIPT EXISTS
#   macOS will not give a STARTING graphical application a render loop unless a display is
#   awake and drawable. Our Avalonia Director needs one at startup, so without it the
#   process dies immediately with "RenderTimer error -6661" — a CoreVideo
#   "invalid argument" from display-link creation. An already-running Director is fine;
#   only a start fails.
#
#   THE CONDITION IS A SLEEPING DISPLAY, NOT A LOCKED SCREEN. An earlier version of this
#   script blamed the lock and then set the display to sleep after ten minutes, calling
#   that "harmless". It is not harmless, it is sufficient on its own, and it cost us a
#   real failure: on 2026-09-03 the launcher installed a new Director at 1:57 in the
#   morning against a dark screen on a machine that was unlocked and awake, watched it
#   fail to start, and pinned a perfectly good build as bad. The machine sat five days on
#   the old version. Avalonia never asks whether the session is unlocked; it asks
#   CoreGraphics for a drawable display.
#
#   So this script does both: never let the machine lock (which every Mac build farm does
#   — GitHub's hosted runners, Cirrus Labs images, Buildkite, Amazon EC2 Mac) AND never
#   let the display sleep. The product no longer depends on either being right: the
#   launcher now wakes the display before it installs and holds the update rather than
#   condemning a build it could not start. These settings keep it from ever having to.
#
# WHAT THIS SCRIPT DOES (each section asks before changing anything)
#   1. Turn FileVault off                  (required for automatic login; decrypts in background)
#   2. Enable automatic login              (machine boots straight to the desktop after any restart)
#   3. Never require a password after sleep or screen saver  (the "do not lock" setting)
#   4. Never start the screen saver        (both in-session and at the login window)
#   5. Power settings                      (never system-sleep, never display-sleep, restart after power failure)
#   6. Software updates                    (keep security data automatic, stop surprise operating-system reboots)
#   7. Quiet-machine settings              (no crash dialogs, no App Nap throttling, no Time Machine disk prompts)
#   8. Enable Remote Login (ssh)           (recovery path when the graphical session is unreachable)
#
# WHAT IT CANNOT DO (one-time clicks in System Settings, listed again at the end)
#   - Grant Full Disk Access / Screen Recording / Accessibility. Those live in the
#     System-Integrity-Protection-protected privacy database and can only be granted by a
#     human in System Settings (or by mobile device management on a supervised machine).
#
# SECURITY TRADEOFF, STATED PLAINLY
#   After this script the disk is unencrypted, the machine logs itself in, and the screen
#   never locks. Anyone with physical access owns the machine and everything on it. This
#   is the accepted posture for a fleet Mac in the owner's own house; do not use this
#   script on a portable machine or in a shared space.
#
# Usage:  bash scripts/mac-unattended-setup.sh          (answers y/n per section; sudo will prompt)
# Re-running is safe — every step is idempotent.
#
set -uo pipefail

bold() { printf '\033[1m%s\033[0m\n' "$*"; }

confirm() {
    # confirm "question" — returns 0 on yes
    local reply
    read -r -p "$1 [y/N] " reply
    [[ "$reply" == "y" || "$reply" == "Y" ]]
}

section() {
    echo
    bold "== $1 =="
}

FLEET_USER="${SUDO_USER:-$USER}"

echo "This script configures THIS Mac as an unattended DevThrottle fleet machine."
echo "Fleet user: $FLEET_USER"
echo "Every section shows the current value and asks before changing it."
echo

# ---------------------------------------------------------------------------
section "1. FileVault off (required for automatic login)"
# ---------------------------------------------------------------------------
fdesetup status
if fdesetup status | grep -q "FileVault is On"; then
    echo "FileVault must be OFF for automatic login to exist at all — macOS disables the"
    echo "automatic-login option while the disk is encrypted, because the pre-boot unlock"
    echo "needs a typed password. Decryption starts now and finishes in the background;"
    echo "the machine stays usable meanwhile."
    if confirm "Turn FileVault OFF now?"; then
        sudo fdesetup disable
        echo "Decryption running in the background. Check progress later with: fdesetup status"
    fi
else
    echo "FileVault is already off — nothing to do."
fi

# ---------------------------------------------------------------------------
section "2. Automatic login (boots straight to the desktop)"
# ---------------------------------------------------------------------------
sudo sysadminctl -autologin status 2>&1 || true
echo "Automatic login means every restart — planned, power failure, or update — lands on"
echo "the logged-in desktop where the Director's launch agent can start it. Without it,"
echo "the machine parks at the login window and someone must type a password."
echo "You will be prompted for $FLEET_USER's account password (stored obfuscated, not"
echo "encrypted, in /etc/kcpassword — recoverable by anyone with root on this machine)."
if confirm "Enable automatic login for $FLEET_USER?"; then
    sudo sysadminctl -autologin set -userName "$FLEET_USER" -password -
    sudo sysadminctl -autologin status
fi

# ---------------------------------------------------------------------------
section "3. Never require a password after sleep or screen saver (the no-lock setting)"
# ---------------------------------------------------------------------------
sysadminctl -screenLock status 2>&1
echo "This is THE setting that fixes graphical launches: with it off, the display turning"
echo "off no longer locks the session, so new applications keep launching normally."
echo "You will be prompted for $FLEET_USER's account password (macOS requires it to weaken"
echo "the lock — this cannot be done silently, by design)."
echo "Note: if System Settings shows this control grayed out, open the iPhone Mirroring"
echo "app and set it to 'Ask every time' first — a known macOS quirk."
if confirm "Set the screen-lock password requirement to Never?"; then
    sysadminctl -screenLock off -password -
    sysadminctl -screenLock status
fi

# ---------------------------------------------------------------------------
section "4. Never start the screen saver"
# ---------------------------------------------------------------------------
echo "Current in-session idle time (missing key means system default):"
defaults -currentHost read com.apple.screensaver idleTime 2>&1 || true
echo "The screen saver serves no purpose on an unattended machine, and on a machine that"
echo "still had locking enabled it would be a lock trigger. Belt and braces: disable it."
if confirm "Disable the screen saver (in-session and at the login window)?"; then
    defaults -currentHost write com.apple.screensaver idleTime -int 0
    sudo defaults write /Library/Preferences/com.apple.screensaver loginWindowIdleTime -int 0
    echo "Screen saver disabled."
fi

# ---------------------------------------------------------------------------
section "5. Power settings (always-on Mac mini)"
# ---------------------------------------------------------------------------
pmset -g custom
echo "Target: the machine never sleeps (sleep 0), disks never spin down (disksleep 0),"
echo "the display NEVER turns off (displaysleep 0), wake-on-network stays on (womp 1),"
echo "and the machine restarts itself after a power failure (autorestart 1)."
echo
echo "displaysleep 0 is the important one and it used to be 10. macOS gives a STARTING"
echo "graphical application no render loop while every display is asleep, so a Director"
echo "restarted against a dark screen dies before its first window. That is what an"
echo "unattended update does at three in the morning. A dark panel is not free."
echo "Note: some Apple Silicon minis honor autorestart inconsistently after a hard power"
echo "cut — the verification list at the end includes a pull-the-plug test."
if confirm "Apply these power settings?"; then
    sudo pmset -a sleep 0
    sudo pmset -a disksleep 0
    sudo pmset -a displaysleep 0
    sudo pmset -a womp 1
    sudo pmset -a autorestart 1
    echo "Applied. New values:"
    pmset -g custom
fi

# ---------------------------------------------------------------------------
section "6. Software updates (no surprise reboots)"
# ---------------------------------------------------------------------------
defaults read /Library/Preferences/com.apple.SoftwareUpdate AutomaticallyInstallMacOSUpdates 2>&1 || true
echo "Keep the invisible security pieces automatic (malware definitions, rapid security"
echo "responses, background downloads) but STOP macOS from installing full operating-system"
echo "updates on its own — those reboot the machine and kill every running session."
echo "Operating-system updates then happen only when you run them during a maintenance window."
if confirm "Apply the update policy (security automatic, operating-system installs manual)?"; then
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate AutomaticCheckEnabled -bool true
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate AutomaticDownload -bool true
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate CriticalUpdateInstall -bool true
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate ConfigDataInstall -bool true
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate AutomaticallyInstallMacOSUpdates -bool false
    sudo defaults write /Library/Preferences/com.apple.commerce AutoUpdate -bool false
    echo "Applied."
fi

# ---------------------------------------------------------------------------
section "7. Quiet-machine settings"
# ---------------------------------------------------------------------------
echo "Three small things that otherwise interrupt an unattended machine:"
echo "  - Crash-report dialogs pile up on screen (crashes still go to the log files)."
echo "  - App Nap throttles applications whose windows are not frontmost."
echo "  - Time Machine asks about every newly attached disk."
if confirm "Apply the quiet-machine settings?"; then
    defaults write com.apple.CrashReporter DialogType none
    defaults write NSGlobalDomain NSAppSleepDisabled -bool YES
    sudo defaults write /Library/Preferences/com.apple.TimeMachine DoNotOfferNewDisksForBackup -bool true
    echo "Applied."
fi

# ---------------------------------------------------------------------------
section "8. Remote Login (ssh) — the recovery path"
# ---------------------------------------------------------------------------
sudo systemsetup -getremotelogin 2>/dev/null || true
echo "When the graphical session is wedged, ssh over Tailscale is how you fix the machine"
echo "without walking to it. Screen Sharing is already enabled on this machine; ssh is the"
echo "second, independent door."
if confirm "Enable Remote Login (ssh)?"; then
    sudo systemsetup -setremotelogin on
    sudo systemsetup -getremotelogin
fi

# ---------------------------------------------------------------------------
section "Done — manual steps that need System Settings clicks"
# ---------------------------------------------------------------------------
cat <<'EOF'
The privacy permissions below live in a System-Integrity-Protection-protected database
and CANNOT be granted from any script. Grant them once; they persist and are inherited
by every process the granted application launches (so the claude processes the Director
spawns are covered by the Director's own grant).

  A. System Settings > Privacy & Security > Full Disk Access
       add "CC Director" (and your terminal application if you run Directors from it).
  B. System Settings > Privacy & Security > Screen Recording
       add "CC Director" — only needed if you use the screenshot features on this Mac.
  C. If any Automation prompt appears later ("CC Director wants to control ..."),
       click Allow once; it is remembered per application pair.

VERIFY THE SETUP (five minutes):
  1. Restart the Mac. It should land on the desktop with nobody touching it.
  2. Turn the display off (or run: pmset displaysleepnow), wait a minute, then launch a
     test-slot Director remotely. It must start cleanly — no RenderTimer error.
  3. Pull the power cord, wait ten seconds, plug it back in. The Mac should boot itself
     and land on the desktop. (Some Apple Silicon minis fail this — if yours does, put it
     on a smart plug or accept a manual power button press after outages.)
  4. From another machine: ssh over Tailscale works, and Screen Sharing still connects.

REBUILD NOTE for slot binaries: the slots are ad-hoc signed, so every rebuild is a "new"
application to macOS. Locally built binaries carry no quarantine flag, so Gatekeeper never
prompts for them — but if the application firewall is ever turned ON, each rebuild will
re-trigger the "accept incoming connections?" prompt until the slots are signed with a
stable self-signed certificate. The firewall is currently off, so nothing to do today.
EOF
