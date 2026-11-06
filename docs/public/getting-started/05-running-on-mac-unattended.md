# Running DevThrottle on a Mac Unattended

A Mac mini makes a great always-on fleet machine -- but macOS ships tuned for a person sitting in front of it, not for an agent host nobody touches for weeks. This page is the checklist that turns a Mac into an unattended DevThrottle Workstation: what to change, the exact commands, and the tradeoffs stated plainly. It assumes DevThrottle is already installed ([Installation](installation.md)) and the machine is reachable over Tailscale ([Multi-Machine Setup](installation.md#multi-machine-setup-remote-access)).

> **The one rule that matters:** never let the screen lock. Turn the display off instead. Everything else on this page is detail.

## Why the screen lock is the enemy

macOS cannot launch a new graphical application while the screen is locked. When the lock engages, the login window takes ownership of the displays. Your session keeps running -- applications that are already open keep working, and background processes are unaffected -- but any **new** graphical launch cannot bind display resources at startup and fails. The Director is a graphical application, so on a locked Mac the fleet cannot start new Directors, and the Director itself cannot be restarted after an update or crash until a human unlocks the screen.

This is architectural, not a bug, and there is no setting, entitlement, or launchd trick that gets around it. We proved it on our own hardware (a Director launched into a locked session dies instantly with a render-timer error, even from a launch agent placed directly in the graphical user domain), and it matches what Apple's own engineers say on the developer forums: the only third-party code that can put an interface on the lock screen is an authorization plug-in that replaces login screens -- there is no mechanism for launching an application behind the lock. Windows is genuinely different by design: locking a Windows machine just switches which desktop is visible, while the session and its window objects stay fully active underneath -- new processes can still create windows there. That is why the Director on Windows launches sessions on locked machines without any special setup, and no equivalent exists on macOS.

The industry already solved this. Every commercial Mac build farm -- GitHub's hosted macOS runners, Cirrus Labs images, Buildkite's Amazon EC2 Mac guide, GitLab's MacStadium images -- ships the identical configuration: automatic login, screen lock disabled, screen saver disabled, system sleep disabled, FileVault off. The display may sleep; the session never locks. That is exactly the configuration below.

## The security tradeoff, stated plainly

After this setup, the machine logs itself in at boot, never locks, and its disk is not encrypted. **Anyone with physical access owns the machine and everything on it.** This is the right trade for a Mac in a locked home or office that only you can touch. It is the wrong trade for a laptop, a shared space, or anywhere the hardware could walk away. Compensating controls that still work: keep the machine reachable only over Tailscale, keep Screen Sharing and Remote Login password-protected, and remember that Find My can remotely lock a stolen machine after the fact.

## The checklist

Everything scriptable is collected in `scripts/mac-unattended-setup.sh` in the repository -- it shows current values and asks per section. The same steps, in order, for doing it by hand:

### 1. Turn FileVault off

Automatic login does not exist while FileVault is on: the encrypted disk needs a typed password before macOS can even start. Decryption runs in the background; the machine stays usable.

    sudo fdesetup disable

*System Settings path:* Privacy & Security > FileVault.
*Tradeoff:* the disk is readable if the hardware is stolen. If that risk is unacceptable, keep FileVault on and accept that every reboot needs a person (for a planned reboot, `sudo fdesetup authrestart` skips the unlock screen exactly once -- it does not help with power failures).

### 2. Enable automatic login

Every restart -- planned, power failure, or crash -- lands on the logged-in desktop where the Director can start.

    sudo sysadminctl -autologin set -userName <user> -password -

*System Settings path:* Users & Groups > Automatically log in as.
*Tradeoff:* the account password is stored obfuscated (not encrypted) in `/etc/kcpassword`.

### 3. Set "require password after sleep or screen saver" to Never

This is the setting that actually prevents locking. With it off, the display turning off no longer locks the session, and new graphical launches keep working around the clock.

    sysadminctl -screenLock off -password -

*System Settings path:* Lock Screen > "Require password after screen saver begins or display is turned off" > Never.
*Gotcha:* if the control is grayed out, open the iPhone Mirroring app and set it to "Ask every time" first.
*Note:* the old `defaults write com.apple.screensaver askForPassword 0` trick stopped working around macOS 13 -- `sysadminctl` is the supported command, and it requires the account password by design.

### 4. Disable the screen saver

    defaults -currentHost write com.apple.screensaver idleTime -int 0
    sudo defaults write /Library/Preferences/com.apple.screensaver loginWindowIdleTime -int 0

*System Settings path:* Lock Screen > "Start Screen Saver when inactive" > Never.

### 5. Power settings: never sleep the machine, let the display sleep

    sudo pmset -a sleep 0            # the machine never sleeps
    sudo pmset -a disksleep 0        # disks never spin down
    sudo pmset -a displaysleep 10    # the display MAY turn off -- harmless once the lock is off
    sudo pmset -a womp 1             # wake on network access
    sudo pmset -a autorestart 1      # start up again after a power failure

*System Settings path:* Energy.
*Gotcha:* some Apple Silicon minis honor "start up after power failure" inconsistently after a hard power cut. Test yours (pull the plug); if it fails, a smart plug that cycles power is the community workaround.

### 6. Software updates: security automatic, reboots manual

Keep the invisible security pieces automatic, but stop macOS from installing full operating-system updates on its own -- those reboot the machine and kill every running session.

    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate AutomaticCheckEnabled -bool true
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate AutomaticDownload -bool true
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate CriticalUpdateInstall -bool true
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate ConfigDataInstall -bool true
    sudo defaults write /Library/Preferences/com.apple.SoftwareUpdate AutomaticallyInstallMacOSUpdates -bool false

Then install operating-system updates yourself, on your schedule: `sudo softwareupdate -ia` during a maintenance window.
*Tradeoff:* you own the patch schedule now -- put a recurring reminder somewhere.

### 7. Quiet-machine settings

    defaults write com.apple.CrashReporter DialogType none                # no crash dialogs (crashes still logged)
    defaults write NSGlobalDomain NSAppSleepDisabled -bool YES            # no App Nap throttling of background apps
    sudo defaults write /Library/Preferences/com.apple.TimeMachine DoNotOfferNewDisksForBackup -bool true

### 8. Enable Remote Login for recovery

When the graphical session is unreachable, `ssh` over Tailscale is how you fix the machine without walking to it. Screen Sharing is the second door -- keep both.

    sudo systemsetup -setremotelogin on

### 9. Privacy permissions: the one-time clicks

The high-value privacy permissions live in a database protected by System Integrity Protection. **No script, no command line, no setting can grant them** on a normal machine -- only a human clicking in System Settings (or mobile device management on a supervised fleet). The good news: a grant is inherited by every process the granted application launches, so granting the Director covers all the agent processes it spawns. Grant once, done forever -- with one caveat: the grant is tied to the application's code signature. From v2.36.0 the released Mac builds are all signed with one unchanging certificate, so a grant survives every automatic update. **Before v2.36.0 it did not**: those builds were ad-hoc signed, which gave every single build a different signature, so each update counted as a brand new application and every permission had to be granted again. A locally rebuilt ad-hoc test slot still counts as a new application for the same reason.

| Permission | Grant it to | Needed for |
|------------|-------------|------------|
| Full Disk Access | the Director (and your terminal, if you launch Directors from one) | agents reading anywhere on disk without per-folder prompts |
| Screen Recording | the Director | the screenshot features |
| Automation | approve prompts as they appear | controlling other applications |

*System Settings path:* Privacy & Security > (each category) > add the app with the + button.

### What you do NOT need to change

- **Gatekeeper** -- locally built binaries never get the quarantine flag, so Gatekeeper never prompts for them. Leave Gatekeeper on; do not use `spctl --global-disable`.
- **The application firewall** -- if it is off, there are no "accept incoming connections?" prompts. If you turn it on, know that ad-hoc-signed binaries get a fresh signature every rebuild, so each rebuild re-prompts; the fix is signing test builds with a stable self-signed certificate, or scripting `socketfilterfw --add --unblockapp` into the build.
- **Notifications** -- banners never block a background process; they only matter to a person looking at the screen.

## Verify the setup

1. Restart the Mac. It must land on the desktop with nobody touching it.
2. Turn the display off (`pmset displaysleepnow`), wait a minute, then start a new session on this machine from the fleet. It must launch cleanly.
3. Pull the power cord for ten seconds. The Mac should boot itself back to the desktop.
4. From another machine: `ssh` works and Screen Sharing connects, both over Tailscale.

## How the rest of the community handles this

You are not alone in this configuration -- it is what everyone running unattended Macs converges on:

- **Mac build farms** (GitHub's hosted macOS runners, Cirrus Labs images, GitLab's MacStadium images, Buildkite's Amazon EC2 Mac guide) all ship automatic login with the lock, screen saver, and sleep disabled. Their provisioning scripts run the same commands listed above.
- **The OpenClaw community** -- thousands of people hosting the open-source agent on dedicated Mac minis -- independently arrived at the identical recipe: automatic login, sleep off, lock and screen saver set to Never, "start up after power failure" on, and the FileVault tradeoff documented just as bluntly as here. Their guides add one more trick for Macs with no monitor: an HDMI dummy plug, because macOS graphical rendering and screen capture misbehave with no display attached at all. If your Mac mini has no monitor, buy the dummy plug.

Browsers deserve a special note, because agents drive them constantly:

- **Headless browsers are immune.** Headless Chromium (Playwright, Puppeteer) never talks to the display, so it launches and runs fine regardless of lock state or missing monitors. OpenClaw's browser tool falls back to headless with a structured error when no display is available.
- **Headed browsers degrade rather than crash.** A visible Chrome window on a locked or backgrounded Mac gets its rendering and timers throttled -- automation pauses and resumes on unlock. Browser-automation people work around it with launch flags (`--disable-background-timer-throttling --disable-renderer-backgrounding`), but on a never-locked machine the problem does not arise.
- **Attaching to an already-running browser keeps working while locked**, because the remote-debugging protocol is a plain network interface and its screenshots come from the browser's own compositor, not the physical screen. This matches the general rule: existing graphical processes survive a lock; only new display-bound launches fail.
- There is **no virtual-display server for macOS** the way Linux has one for X11 -- a dummy plug or virtual-display utility is the closest substitute, and neither bypasses the lock screen.

## Next Steps

- [Installation](installation.md) -- installing DevThrottle on macOS
- [Multi-Machine Setup](installation.md#multi-machine-setup-remote-access) -- joining the fleet over Tailscale
- [Quick Start](quick-start.md) -- your first session
