# DevThrottle - install prompt (Windows + macOS)

The **prompt you hand to Claude Code** to download and install the latest DevThrottle. One prompt,
OS-aware: the agent detects the platform and installs the right asset to a **per-user, user-writable**
location so the built-in **auto-update can replace it in place with no admin/sudo**. Never
`Program Files` (Windows) or `/Applications` (macOS). Philosophy: [../PHILOSOPHY.md](../PHILOSOPHY.md).

Copy everything in the block below into a Claude Code session on the target machine.

---

```text
Install the latest release of DevThrottle on THIS machine. You are doing the install yourself -
no installer wizard, no admin/sudo. Detect the OS and follow the matching section. STOP with a
clear message if any step fails; do not silently work around it or build from source.

REPO: github.com/thefrederiksen/devthrottle
Find the latest release - prefer `gh release view --repo thefrederiksen/devthrottle --json tagName,assets`,
else the public API https://api.github.com/repos/thefrederiksen/devthrottle/releases/latest. It must
include `release-manifest.json` plus this OS's asset below. ALWAYS verify the downloaded asset's
SHA-256 against the manifest's entry for that asset before installing; mismatch = STOP.

== WINDOWS ==
ASSET:  cc-director-win-x64.exe        (self-contained; no .NET needed)
TARGET: %LOCALAPPDATA%\cc-director\app\cc-director.exe   (user-writable -> auto-update needs no admin)
1. Download cc-director-win-x64.exe + release-manifest.json to %TEMP%\ccd-install.
2. Verify: Get-FileHash -Algorithm SHA256 == manifest sha256 for cc-director-win-x64.exe, else STOP.
3. Create %LOCALAPPDATA%\cc-director\app. If cc-director.exe is there AND running, ask the user to
   close it (do not kill it).
4. Copy the verified exe to %LOCALAPPDATA%\cc-director\app\cc-director.exe.
5. Start Menu shortcut: %APPDATA%\Microsoft\Windows\Start Menu\Programs\DevThrottle.lnk -> the exe
   (working dir = its folder). OPTIONAL autostart on login: also drop a shortcut in
   %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup.

== macOS (Apple Silicon) ==
ASSET:  cc-director-mac-arm64.zip      (contains "CC Director.app"; self-contained)
TARGET: ~/Applications/CC Director.app  (user-writable, NOT /Applications -> auto-update needs no sudo)
1. Download cc-director-mac-arm64.zip + release-manifest.json to /tmp/ccd-install.
2. Verify: `shasum -a 256` of the zip == manifest sha256 for cc-director-mac-arm64.zip, else STOP.
3. Unzip it. `mkdir -p ~/Applications`. If "~/Applications/CC Director.app" is running, ask the user
   to quit it (do not kill it).
4. Replace: `rm -rf "~/Applications/CC Director.app"` then move the unzipped "CC Director.app" into
   ~/Applications.
5. Clear Gatekeeper quarantine: `xattr -dr com.apple.quarantine "~/Applications/CC Director.app"`.
   It's now in Launchpad/Spotlight. OPTIONAL: add to the Dock; OPTIONAL autostart via Login Items.

== BOTH ==
6. Launch it once and confirm the running version matches the release tag (check the newest log under
   %LOCALAPPDATA%\cc-director\logs\director\ on Windows, or the app's log dir on macOS).
7. Report: release tag installed, install path, the SHA you verified, and the shortcut/Dock entry.
   Note the runtime prerequisites if not set up: a Claude subscription (for Claude Code) and an
   OpenAI API key (audio/transcription/TTS) in the cc-director config dir
   (%LOCALAPPDATA%\cc-director\config\credentials.env on Windows; the equivalent config dir on macOS).

DO NOT: use Program Files or /Applications, require admin/sudo, build from source, or skip SHA verification.
```

---

## Why this shape

- **Per-user, user-writable target** (`%LOCALAPPDATA%\cc-director\app` / `~/Applications`) - the auto-updater overwrites the running app's own path, so a user-writable location means updates need **no admin/sudo**. `Program Files` / `/Applications` would force elevation on every update or fail.
- **Self-contained assets** - no .NET install step on either OS.
- **SHA-256 against `release-manifest.json`** - the same trust check the auto-updater uses, so install and update share one verification.
- **One OS-aware prompt** - hand the same prompt to any machine (this Windows box, the Mac-mini, Windows-2); the agent picks the right branch.

## Try it / next steps

1. Run the prompt in Claude Code on the target machine. Current latest is **v0.3.3** (has both `cc-director-win-x64.exe` and `cc-director-mac-arm64.zip` + manifest).
2. Cut a newer release; launch the installed build and confirm **auto-update** pulls it (the build must be a CI release build, i.e. `UpdaterEnabled=true`).
